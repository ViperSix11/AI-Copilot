#include <windows.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
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
    constexpr wchar_t PipeName[] = LR"(\\.\pipe\AICopilot.Arma3.Telemetry)";
    constexpr std::size_t MaximumQueueSize = 256;
    constexpr char Version[] = "0.1.0";

    std::once_flag startFlag;
    std::atomic_bool connected{false};
    std::atomic_uint64_t droppedMessages{0};
    std::mutex queueMutex;
    std::condition_variable queueChanged;
    std::deque<std::string> messageQueue;

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
            GENERIC_WRITE,
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

        return remaining == 0;
    }

    void WorkerLoop()
    {
        HANDLE pipe = INVALID_HANDLE_VALUE;

        while (true)
        {
            std::string message;
            {
                std::unique_lock lock(queueMutex);
                queueChanged.wait(lock, [] { return !messageQueue.empty(); });

                message = std::move(messageQueue.front());
                messageQueue.pop_front();
            }

            while (pipe == INVALID_HANDLE_VALUE)
            {
                pipe = TryConnectToPipe();
                if (pipe != INVALID_HANDLE_VALUE)
                {
                    break;
                }

                // Telemetry is state, not a durable event stream. While disconnected,
                // keep the most recent snapshot and discard older queued snapshots.
                {
                    std::lock_guard lock(queueMutex);
                    if (!messageQueue.empty())
                    {
                        message = std::move(messageQueue.back());
                        messageQueue.clear();
                    }
                }
                Sleep(150);
            }

            if (!WriteMessage(pipe, message))
            {
                CloseHandle(pipe);
                pipe = INVALID_HANDLE_VALUE;
                connected.store(false);

                std::lock_guard lock(queueMutex);
                if (messageQueue.size() >= MaximumQueueSize)
                {
                    messageQueue.pop_front();
                    droppedMessages.fetch_add(1);
                }
                messageQueue.push_front(std::move(message));
            }
        }

        if (pipe != INVALID_HANDLE_VALUE)
        {
            CloseHandle(pipe);
        }
        connected.store(false);
    }

    void EnsureStarted()
    {
        std::call_once(startFlag, []
        {
            std::thread(WorkerLoop).detach();
        });
    }

    void Enqueue(std::string message)
    {
        EnsureStarted();
        {
            std::lock_guard lock(queueMutex);
            if (messageQueue.size() >= MaximumQueueSize)
            {
                messageQueue.pop_front();
                droppedMessages.fetch_add(1);
            }
            messageQueue.push_back(std::move(message));
        }
        queueChanged.notify_one();
    }

    std::string StatusJson()
    {
        std::lock_guard lock(queueMutex);
        std::ostringstream stream;
        stream << "{\"connected\":" << (connected.load() ? "true" : "false")
               << ",\"queued\":" << messageQueue.size()
               << ",\"dropped\":" << droppedMessages.load() << "}";
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
        else if (command.rfind("telemetry|", 0) == 0)
        {
            Enqueue(command.substr(10));
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

        CopyOutput(output, outputSize, "unknown-command");
        return -1;
    }
}

