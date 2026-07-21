#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <utility>

namespace
{
    constexpr wchar_t PipeName[] = LR"(\\.\pipe\ArmaAiBridge.Arma3.Telemetry)";
    constexpr std::size_t MaximumOutboundQueueSize = 256;
    constexpr std::size_t MaximumInboundQueueSize = 64;
    constexpr std::size_t MaximumInboundBufferSize = 1024 * 1024;
    constexpr char Version[] = "0.2.0";

    struct OutboundMessage
    {
        std::string value;
        bool telemetrySnapshot;
    };

    std::once_flag startFlag;
    std::atomic_bool connected{false};
    std::atomic_uint64_t droppedOutboundMessages{0};
    std::atomic_uint64_t droppedInboundCommands{0};

    std::mutex outboundMutex;
    std::deque<OutboundMessage> outboundQueue;

    std::mutex inboundMutex;
    std::deque<std::string> inboundQueue;

    void CopyOutput(char* output, unsigned int outputSize, const std::string& value)
    {
        if (output == nullptr || outputSize == 0)
        {
            return;
        }

        const std::size_t count = std::min<std::size_t>(value.size(), outputSize - 1);
        std::memcpy(output, value.data(), count);
        output[count] = '\0';
    }

    HANDLE TryConnectToPipe()
    {
        if (!WaitNamedPipeW(PipeName, 100))
        {
            connected.store(false);
            return INVALID_HANDLE_VALUE;
        }

        HANDLE pipe = CreateFileW(
            PipeName,
            GENERIC_READ | GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);

        connected.store(pipe != INVALID_HANDLE_VALUE);
        return pipe;
    }

    bool WriteMessage(HANDLE pipe, const std::string& message)
    {
        std::string framed = message;
        framed.push_back('\n');

        const char* cursor = framed.data();
        std::size_t remaining = framed.size();

        while (remaining > 0)
        {
            DWORD written = 0;
            const DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(remaining, 64 * 1024));
            if (!WriteFile(pipe, cursor, chunk, &written, nullptr) || written == 0)
            {
                return false;
            }

            cursor += written;
            remaining -= written;
        }

        return true;
    }

    void QueueInboundCommand(std::string command)
    {
        if (command.empty())
        {
            return;
        }

        std::lock_guard lock(inboundMutex);
        if (inboundQueue.size() >= MaximumInboundQueueSize)
        {
            inboundQueue.pop_front();
            droppedInboundCommands.fetch_add(1);
        }
        inboundQueue.push_back(std::move(command));
    }

    bool ReadAvailableCommands(HANDLE pipe, std::string& buffer)
    {
        DWORD available = 0;
        if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
        {
            return false;
        }

        while (available > 0)
        {
            char chunk[4096];
            DWORD read = 0;
            const DWORD requested = std::min<DWORD>(available, static_cast<DWORD>(sizeof(chunk)));
            if (!ReadFile(pipe, chunk, requested, &read, nullptr) || read == 0)
            {
                return false;
            }

            buffer.append(chunk, read);
            if (buffer.size() > MaximumInboundBufferSize)
            {
                buffer.clear();
                droppedInboundCommands.fetch_add(1);
            }

            std::size_t newline = std::string::npos;
            while ((newline = buffer.find('\n')) != std::string::npos)
            {
                std::string line = buffer.substr(0, newline);
                if (!line.empty() && line.back() == '\r')
                {
                    line.pop_back();
                }
                buffer.erase(0, newline + 1);
                QueueInboundCommand(std::move(line));
            }

            if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
            {
                return false;
            }
        }

        return true;
    }

    void RequeueFront(OutboundMessage message)
    {
        std::lock_guard lock(outboundMutex);
        if (outboundQueue.size() >= MaximumOutboundQueueSize)
        {
            outboundQueue.pop_back();
            droppedOutboundMessages.fetch_add(1);
        }
        outboundQueue.push_front(std::move(message));
    }

    bool TryPopOutbound(OutboundMessage& message)
    {
        std::lock_guard lock(outboundMutex);
        if (outboundQueue.empty())
        {
            return false;
        }
        message = std::move(outboundQueue.front());
        outboundQueue.pop_front();
        return true;
    }

    void WorkerLoop()
    {
        HANDLE pipe = INVALID_HANDLE_VALUE;
        std::string inboundBuffer;

        while (true)
        {
            if (pipe == INVALID_HANDLE_VALUE)
            {
                pipe = TryConnectToPipe();
                if (pipe == INVALID_HANDLE_VALUE)
                {
                    Sleep(150);
                    continue;
                }
                inboundBuffer.clear();
            }

            OutboundMessage outbound;
            if (TryPopOutbound(outbound))
            {
                if (!WriteMessage(pipe, outbound.value))
                {
                    RequeueFront(std::move(outbound));
                    CloseHandle(pipe);
                    pipe = INVALID_HANDLE_VALUE;
                    connected.store(false);
                    continue;
                }
            }

            if (!ReadAvailableCommands(pipe, inboundBuffer))
            {
                CloseHandle(pipe);
                pipe = INVALID_HANDLE_VALUE;
                connected.store(false);
                continue;
            }

            Sleep(5);
        }
    }

    void EnsureStarted()
    {
        std::call_once(startFlag, []
        {
            std::thread(WorkerLoop).detach();
        });
    }

    void Enqueue(std::string message, bool telemetrySnapshot)
    {
        EnsureStarted();
        std::lock_guard lock(outboundMutex);

        if (telemetrySnapshot)
        {
            auto existing = std::find_if(outboundQueue.rbegin(), outboundQueue.rend(), [](const OutboundMessage& queued)
            {
                return queued.telemetrySnapshot;
            });
            if (existing != outboundQueue.rend())
            {
                existing->value = std::move(message);
                return;
            }
        }

        if (outboundQueue.size() >= MaximumOutboundQueueSize)
        {
            auto telemetry = std::find_if(outboundQueue.begin(), outboundQueue.end(), [](const OutboundMessage& queued)
            {
                return queued.telemetrySnapshot;
            });
            if (telemetry != outboundQueue.end())
            {
                outboundQueue.erase(telemetry);
            }
            else
            {
                outboundQueue.pop_front();
            }
            droppedOutboundMessages.fetch_add(1);
        }

        outboundQueue.push_back({std::move(message), telemetrySnapshot});
    }

    std::string PollCommand()
    {
        std::lock_guard lock(inboundMutex);
        if (inboundQueue.empty())
        {
            return "";
        }

        std::string command = std::move(inboundQueue.front());
        inboundQueue.pop_front();
        return command;
    }

    std::string StatusJson()
    {
        std::scoped_lock lock(outboundMutex, inboundMutex);
        std::ostringstream stream;
        stream << "{\"connected\":" << (connected.load() ? "true" : "false")
               << ",\"outboundQueued\":" << outboundQueue.size()
               << ",\"inboundQueued\":" << inboundQueue.size()
               << ",\"droppedOutbound\":" << droppedOutboundMessages.load()
               << ",\"droppedInbound\":" << droppedInboundCommands.load() << "}";
        return stream.str();
    }
}

extern "C"
{
    __declspec(dllexport) void __stdcall RVExtensionVersion(char* output, unsigned int outputSize)
    {
        EnsureStarted();
        CopyOutput(output, outputSize, Version);
    }

    __declspec(dllexport) void __stdcall RVExtension(char* output, unsigned int outputSize, const char* function)
    {
        EnsureStarted();
        const std::string command = function == nullptr ? "" : function;

        if (command == "ping")
        {
            CopyOutput(output, outputSize, "pong");
        }
        else if (command == "status")
        {
            CopyOutput(output, outputSize, StatusJson());
        }
        else if (command == "poll")
        {
            CopyOutput(output, outputSize, PollCommand());
        }
        else if (command.rfind("telemetry|", 0) == 0)
        {
            Enqueue(command.substr(10), true);
            CopyOutput(output, outputSize, "queued");
        }
        else if (command.rfind("query-result|", 0) == 0)
        {
            Enqueue(command.substr(13), false);
            CopyOutput(output, outputSize, "queued");
        }
        else
        {
            CopyOutput(output, outputSize, "unknown-command");
        }
    }

    __declspec(dllexport) int __stdcall RVExtensionArgs(
        char* output,
        unsigned int outputSize,
        const char* function,
        const char** argv,
        unsigned int argc)
    {
        EnsureStarted();
        const std::string command = function == nullptr ? "" : function;

        if (command == "status")
        {
            CopyOutput(output, outputSize, StatusJson());
            return 0;
        }

        if (command == "poll")
        {
            CopyOutput(output, outputSize, PollCommand());
            return 0;
        }

        CopyOutput(output, outputSize, "unknown-command");
        return -1;
    }
}
