# Third-party notices

ResourceAlerter is licensed under the MIT License (see [LICENSE](LICENSE)). It depends on the
following third-party components, distributed under their own licenses:

## LibreHardwareMonitorLib

- License: Mozilla Public License 2.0 (MPL-2.0)
- Project: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- Used unmodified as a NuGet package to read CPU temperature and PSU voltage sensors. MPL-2.0
  is a file-level (weak) copyleft license: it does not extend to the rest of this project, and
  since its source is not modified here, no additional source-disclosure obligation applies
  beyond what LibreHardwareMonitor itself already provides upstream.

## SQLite

- License: Public domain
- Project: https://www.sqlite.org/
- The native SQLite engine, bundled transitively via `Microsoft.Data.Sqlite` /
  `SQLitePCLRaw`, used for local data recording.

## MIT-licensed packages

The following NuGet packages are used under the MIT License:

- `Microsoft.Data.Sqlite` — https://github.com/dotnet/efcore
- `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Hosting.WindowsServices`,
  `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.Configuration.Binder`,
  `Microsoft.Extensions.Configuration.Json` — https://github.com/dotnet/runtime
- `System.Diagnostics.PerformanceCounter`, `System.Management` — https://github.com/dotnet/runtime
- `ScottPlot`, `ScottPlot.WinForms` — https://github.com/ScottPlot/ScottPlot
- `OpenTK`, `OpenTK.GLControl` (transitive dependency of `ScottPlot.WinForms`, used for the
  Viewer's chart rendering surface) — https://github.com/opentk/opentk

Full license texts for each package are included in their respective NuGet packages and
upstream repositories.
