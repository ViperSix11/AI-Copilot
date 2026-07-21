# Repository audit

This audit accompanies the rename to **ArmA AI Bridge**.

The automated repository verifier checks:

- required application, native extension, Arma addon, schema and workflow paths
- absence of obsolete product identifiers in tracked UTF-8 files
- JSON, XAML, project and manifest syntax
- matching telemetry schema identifiers
- matching Named Pipe names across C#, C++ and PowerShell
- matching CMake target, Arma extension and addon names
- current paths in the Windows build workflow

The GitHub Actions Windows job additionally publishes the .NET 8 WPF application, configures and builds the x64 C++ extension, verifies the DLL output and assembles the development artifact.
