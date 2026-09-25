# Detective — Implementation Plan

A native WinUI 3 performance monitor for CPU, memory, disks, networks and GPUs. It follows the look and layout of the Windows 11 Task Manager **Performance** page.

> **Status (2026-09-26):** M0–M5 are built and verified on the dev machine (i9-12900KF, 128 GB DDR4, 10 disks, Wi-Fi 6, Arc A770). M6 is partly done: settings, persistence, theme and selection sync are in; keyboard shortcuts and a Narrator pass are not. M7 is not started.
> Decisions from §10 are recorded there. Where the build diverged from this plan, the section is marked **As built**.

---

## 1. Goals and non-goals

**Goals**
- Live, low-overhead monitoring of CPU, Memory, Disks (per mounted volume), Networks (per adapter) and GPUs (per adapter).
- A Windows 11 look: Mica backdrop, Segoe UI Variable, Fluent icons, filled area charts, light/dark theme following the system.
- One refresh rate for every chart, set from the Settings button at the bottom of the left rail.
- Runs as a standard user, with no admin rights and no kernel driver.
- Detective's own footprint stays under ~1% CPU and ~80 MB RAM at a 1 s refresh. *Measured:* 0.06% of total CPU (1.4% of one core). Memory is 210 MB working set / 152 MB private in a Debug build, so the RAM target is **not met**. That is mostly the self-contained WinUI/.NET baseline.

**Non-goals (v1)**
- The Processes, Services, Startup and Users tabs.
- Historical logging or export (possible v2).
- Reading RAM timings from SPD. This needs SMBus access through a ring-0 driver. See §9.

---

## 2. Technology stack

| Concern | Choice | Notes |
|---|---|---|
| Language/runtime | C# / .NET 10 (SDK 10.0.4xx is installed) | `net10.0-windows10.0.22621.0`, x64 (**as built**: ARM64 not configured yet) |
| UI | Windows App SDK 1.7 (WinUI 3) | **As built:** unpackaged and self-contained, because it is a personal sideload |
| MVVM | CommunityToolkit.Mvvm | `ObservableObject`, `[ObservableProperty]`, `RelayCommand` |
| Charts | **As built:** XAML shapes (`Polygon` / `Polyline` / `Path`) | Custom `AreaChart` control with no Win2D dependency; see §6 |
| Perf counters | PDH via hand-written P/Invoke (`PdhOpenQuery`, `PdhAddEnglishCounter`, `PdhGetFormattedCounterArray`) | English counter names, so it works on any locale |
| Static hardware info | `System.Management` (WMI/CIM), SetupAPI/CfgMgr32, DXGI, WLAN API, CPUID | Queried once, off the UI thread |
| Interop | **As built:** hand-written `DllImport`s; DXGI is called through raw vtable function pointers | No CsWin32 and no COM interop marshalling |
| Composition | **As built:** plain construction, no DI container | Only one `Sampler` and one `ShellVm` exist |
| Tests | xUnit + FluentAssertions | Pure logic and parsers; providers behind interfaces |

Packaging, **as built:** unpackaged and self-contained, run straight from the build output. Settings are stored as JSON in `%LOCALAPPDATA%\Detective\settings.json`. Wi-Fi SSID follows the system switch "Let desktop apps access your location" (see §9).

---

## 3. Solution layout

```
Detective.sln
src/
  Detective/                         WinUI 3 app (packaged)
    App.xaml(.cs)                    DI container, theme, startup
    MainWindow.xaml(.cs)             Mica, custom title bar, shell layout
    Shell/
      ShellViewModel.cs              selected focus item, refresh-rate, item list
      FocusItem.cs                   (Kind, InstanceId, Label, SummarySeries)
    Views/
      CpuView.xaml                   upper section, per focus kind
      MemoryView.xaml
      DiskView.xaml
      NetworkView.xaml
      GpuView.xaml
      SummaryStrip.xaml              lower section: horizontal tile strip
      SettingsFlyout.xaml
    ViewModels/                      CpuViewModel, MemoryViewModel, ... (one per kind)
    Controls/
      AreaChart.cs                   Win2D filled area chart (single/dual series)
      MemoryCompositionBar.xaml      In use | Modified | Standby | Free
      DetailsPanel.xaml              big-stat + key/value grid block
    Themes/Colors.xaml               per-kind accent brushes (light/dark)
  Detective.Core/                    no UI dependency
    Sampling/
      Sampler.cs                     PeriodicTimer loop; publishes Snapshot
      RingBuffer.cs                  fixed-capacity series (60 samples)
      Snapshot.cs                    immutable per-tick data
    Providers/
      ICpuProvider / CpuProvider.cs
      IMemoryProvider / MemoryProvider.cs
      IDiskProvider / DiskProvider.cs
      INetworkProvider / NetworkProvider.cs
      IGpuProvider / GpuProvider.cs
    Pdh/PdhQuery.cs                  thin wrapper: add counters, collect, read arrays
    Hardware/                        static-info readers (WMI, CPUID, DXGI, SetupAPI, WLAN)
    Settings/SettingsService.cs
tests/
  Detective.Core.Tests/
```

---

## 4. Shell layout (Task Manager / File Explorer hybrid)

```
┌───────────────────────────────────────────────────────────────────┐
│ [icon] Detective                                    ─  ▢  ✕      │ custom title bar (Mica)
├────┬──────────────────────────────────────────────────────────────┤
│ ▣  │  CPU                              AMD Ryzen 9 7950X 16-Core   │ ┐
│CPU │  % Utilization                                        100%    │ │
│ ▤  │  ┌──────────────────────────────────────────────────────┐    │ │ UPPER
│Mem │  │▁▂▃▅▆▅▃▂▁▂▃▅▇█▇▅▃▂▁▂▃▅▆▅▃▂▁      (filled area chart)   │    │ │ ≥ 70% of height
│ ◫  │  └──────────────────────────────────────────────────────┘    │ │
│Disk│  60 seconds                                              0   │ │
│ ⇅  │  Utilization  Speed      │ Base speed:     4.50 GHz          │ │
│Net │  14%          5.12 GHz   │ Sockets:        1                 │ │
│ ▦  │  Processes    Threads    │ Cores:          16                │ │
│GPU │  312          4,810      │ Logical procs:  32   ...          │ ┘
│    ├──────────────────────────────────────────────────────────────┤
│    │ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐   ◀ ▶       │ ┐ LOWER
│    │ │▁▃▅▃│ │▅▅▅▅│ │▁▁▃▁│ │▁▂▁▁│ │▁▁▁▅│ │▂▃▂▁│ │▃▅▃▂│             │ │ ≤ 300 px
│ ⚙  │ └────┘ └────┘ └────┘ └────┘ └────┘ └────┘ └────┘             │ │ scrolls horizontally
│    │  CPU   Memory  C:     D:    Wi-Fi  Ethernet GPU 0            │ ┘
└────┴──────────────────────────────────────────────────────────────┘
```

**Left rail.** A `NavigationView` with `PaneDisplayMode="LeftCompact"`, `IsPaneToggleButtonVisible` set as desired, and `IsBackButtonVisible="Collapsed"`. The five items use Fluent icon glyphs (Segoe Fluent Icons: CPU ``, Memory ``, Disk ``, Network ``, GPU ``). Tooltips give the names. The built-in settings item (`IsSettingsVisible=true`) sits at the bottom and opens the **Settings flyout**, not a page.

**Right pane.** A `Grid` with two rows:
- Row 0 (`*`) is the upper focus area. It hosts a `ContentControl` whose content is the view for the selected `FocusItem`. A `DataTemplateSelector` chooses the view by `FocusItem.Kind`.
- Row 1 is the lower summary strip. Its height is recomputed on `SizeChanged` as `min(300, 0.30 × paneHeight)`, so the upper area always keeps at least 70%.

**Summary strip.** An `ItemsRepeater` with `StackLayout Orientation=Horizontal` inside a horizontal `ScrollViewer`. Each tile is a mini `AreaChart` with its label below: `CPU`, `Memory`, a drive letter such as `C:`, a network type such as `Wi-Fi` or `Ethernet`, and `GPU 0` or `GPU 1`. The selected tile gets the accent selection border that Task Manager uses. Tiles have a fixed aspect ratio, and their height follows the strip height.

**Selection model.** `ShellViewModel.SelectedItem` is the single source of truth.
- Clicking a **summary tile** selects that exact instance.
- Clicking a **rail icon** selects the most recently used instance of that kind, or the first one if none was used. The rail highlight and tile highlight stay in sync both ways.
- If a selected volume or adapter disappears (USB drive unplugged, VPN dropped), selection falls back to the first item of the same kind, then to CPU.

---

## 5. Data collection

### 5.1 Sampling engine
- One `Sampler` runs a `PeriodicTimer` at the configured interval on a background task.
- Each tick collects one shared PDH query (`PdhCollectQueryData`) plus the non-PDH sources (GetIfTable2 and GlobalMemoryStatusEx). It then builds an immutable `Snapshot` and posts it to the UI through `DispatcherQueue.TryEnqueue`.
- Every series is a `RingBuffer<float>` with a fixed capacity of **60 samples**, as in Task Manager. The x-axis caption is computed from the interval: 60 s at 1 s, 30 s at 0.5 s, 4 min at 4 s.
- Rate counters need two samples, so the first tick only primes them.
- **Paused** stops the timer. The charts freeze and keep their history.
- Static hardware info is loaded once at startup, asynchronously. Until it arrives, the details blocks show "—". Nothing in the per-tick path calls WMI.
- Device changes are handled by rescanning volumes, network adapters and GPUs. Triggers are `NetworkChange.NetworkAddressChanged`, a WMI `Win32_VolumeChangeEvent` watcher, and a 10 s fallback timer. PDH counter instances are then re-added.

### 5.2 CPU

| Data | Source |
|---|---|
| Overall utilization | PDH `\Processor Information(_Total)\% Processor Utility`. This is the counter Task Manager uses, and it can go above 100% on turbo, so clamp it to 100 for the chart. |
| Per-logical-processor utilization | PDH `\Processor Information(*)\% Processor Utility`. Parse instances like `0,5`, drop `_Total` and `0,_Total` rows, and sort by group then index. |
| Current speed | `\Processor Information(_Total)\% Processor Performance` × base MHz / 100 |
| Processes / threads / handles | `GetPerformanceInfo` (`ProcessCount`, `ThreadCount`, `HandleCount`) |
| Up time | `Environment.TickCount64` |
| Name, manufacturer, base speed, sockets, cores, logical processors, L1/L2/L3 | `Win32_Processor` and `GetLogicalProcessorInformationEx` (for cache sizes and core/package counts) |
| Family / model / stepping | `X86Base.CpuId(1,0)` on x64. On ARM64, use `Win32_Processor.Caption`/`Description` and the registry `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0` |
| Virtualization | `IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED)` and `Win32_ComputerSystem.HypervisorPresent` |

The view has two modes: **Overall utilization** (one chart) and **Logical processors** (a grid of small charts). A segmented toggle in the header switches between them, and the right-click context menu offers the same choice, as in Task Manager. The grid uses `UniformGridLayout`. Its column count is `ceil(sqrt(n × aspect))`.

### 5.3 Memory

| Data | Source |
|---|---|
| Total / available / in use | `GlobalMemoryStatusEx` |
| Committed / commit limit, cached, paged/non-paged pool | `GetPerformanceInfo`, plus PDH `\Memory\Pool Paged Bytes` and `\Memory\Pool Nonpaged Bytes` |
| Composition (In use / Modified / Standby / Free) | PDH `\Memory\Modified Page List Bytes`, `\Memory\Standby Cache Core Bytes`, `Standby Cache Normal Priority Bytes` and `Standby Cache Reserve Bytes` (summed), and `\Memory\Free & Zero Page List Bytes`. In use = Total − (Modified + Standby + Free). |
| Hardware reserved | Installed (`GetPhysicallyInstalledSystemMemory`) − OS-visible total |
| Speed, manufacturer, part number, capacity per DIMM, form factor, type (DDR4/DDR5) | `Win32_PhysicalMemory` (`Speed`, `ConfiguredClockSpeed`, `Manufacturer`, `PartNumber`, `SMBIOSMemoryType`, `DeviceLocator`) |
| Slots used / total | Count of `Win32_PhysicalMemory` rows / `Win32_PhysicalMemoryArray.MemoryDevices` |
| Timings | Not available without a kernel driver (see §9). Show configured speed and voltage (from raw SMBIOS type 17 via `GetSystemFirmwareTable('RSMB')`) instead. |

The view has two parts: a memory-usage area chart, then a **Memory composition** horizontal bar with hover tooltips, then the details block.

### 5.4 Disks

**Decided: one item per physical disk, with its volumes rolled up into it.** A tile is labelled with its drive letters, for example `C: F:`. The focus view shows the disk's active time and transfer rate, the disk's hardware, and one row per volume.

| Data | Source |
|---|---|
| Volumes | `Win32_Volume` (with DriveLetter, FileSystem, Capacity, FreeSpace, DriveType in 2 and 3) |
| Read / write rate per volume | PDH `\LogicalDisk(C:)\Disk Read Bytes/sec` and `Disk Write Bytes/sec` |
| Active time | PDH `\LogicalDisk(C:)\% Idle Time`, taking 100 − idle. For physical-disk parity with Task Manager, `\PhysicalDisk(0 C:)\% Idle Time` is also collected. |
| Avg response time | `\LogicalDisk(*)\Avg. Disk sec/Transfer` |
| Volume → physical disk | `DeviceIoControl(IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS)` → disk number |
| Manufacturer, model, interface (NVMe/SATA/USB/SAS), type (SSD/HDD), capacity | `MSFT_PhysicalDisk` in `root\Microsoft\Windows\Storage` (`FriendlyName`, `Manufacturer`, `Model`, `BusType`, `MediaType`, `Size`). Fall back to `IOCTL_STORAGE_QUERY_PROPERTY`. |
| Format, capacity, free, system/page-file flags | `Win32_Volume` |

The focus view has two charts, as in Task Manager: **Active time** (%) and **Disk transfer rate**. The transfer chart has a Read series (solid fill) and a Write series (dashed line). Its y-axis auto-scales to a "nice" maximum (see §6).

### 5.5 Networks

| Data | Source |
|---|---|
| Adapters | **Decided: physical adapters only.** The list is the connected adapters from `NetworkInterface.GetAllNetworkInterfaces()` where `MSFT_NetAdapter.HardwareInterface = TRUE`. It is rescanned when addresses change and every 10 s. |
| Send / receive rate | Delta of `GetIPStatistics()` byte counters (GetIfEntry2 underneath) ÷ elapsed seconds |
| Type label (Ethernet / Wi-Fi / Bluetooth / Cellular / VPN) | `MIB_IF_ROW2.Type` + `PhysicalMediumType` |
| Manufacturer / model | `MSFT_NetAdapter` (`DriverProvider`, `InterfaceDescription`, `LinkSpeed`) |
| IPv4, IPv6, DNS, DNS suffix | `NetworkInterface.GetIPProperties()` |
| SSID, signal, band | `WlanOpenHandle` → `WlanQueryInterface(wlan_intf_opcode_current_connection)` |

The focus view has one **Throughput** chart with Send and Receive series and an auto-scaled y-axis (Kbps/Mbps/Gbps). The header shows the adapter name and type. Summary tiles are labelled with the type; a duplicate type gets a suffix (`Ethernet 2`).

### 5.6 GPUs

| Data | Source |
|---|---|
| Adapters | DXGI `IDXGIFactory6::EnumAdapterByGpuPreference`, skipping the Microsoft Basic Render Driver. Keyed by **LUID**. |
| Engine utilization | PDH `\GPU Engine(*)\Utilization Percentage`. Instance names look like `pid_1234_luid_0x0000_0x0001ABCD_phys_0_eng_3_engtype_3D`. Parse `luid` and `engtype`, sum across processes per (luid, engine index), and take the max per engine type. This matches Task Manager. |
| Engine types shown | `3D`, `Compute_0`/`Compute_1` (shown as "Compute"), `VideoDecode`, `VideoEncode`, plus `Copy` for completeness. |
| Dedicated / shared memory usage | PDH `\GPU Adapter Memory(*)\Dedicated Usage` and `Shared Usage` (instance per LUID) |
| Dedicated / shared budget | `IDXGIAdapter3::QueryVideoMemoryInfo` (LOCAL / NON_LOCAL), `DXGI_ADAPTER_DESC1.DedicatedVideoMemory` |
| Manufacturer / model / driver version / date | `DXGI_ADAPTER_DESC1` (VendorId → name map), SetupAPI `DEVPKEY_Device_DriverVersion`, `DEVPKEY_Device_DriverDate` |
| PCIe details | Match the adapter to its PnP device node, via `D3DKMT_OPENADAPTERFROMLUID` + `D3DKMTQueryAdapterInfo(KMTQAITYPE_PHYSICALADAPTERDEVICEIDS)`, or by VEN/DEV match. Then read `DEVPKEY_PciDevice_CurrentLinkSpeed`, `CurrentLinkWidth`, `MaxLinkSpeed`, `MaxLinkWidth` and `DEVPKEY_Device_LocationInfo` (bus/device/function). |
| DirectX version, temperature (optional) | `D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA)` for temperature, where the driver supports it |

The focus view has a 2×2 grid of engine charts (**3D, Compute, Video Decode, Video Encode**). Each engine chart has a dropdown on its title to swap in another engine, as in Task Manager. Below them are two wide charts, **Dedicated GPU memory usage** and **Shared GPU memory usage**, then the details block.

**Summary tile metric (decided):** the *busiest* engine, as Task Manager does. Unnamed engines count toward it but are not offered in the engine pickers. If an adapter lacks a default engine, the view substitutes one it has; for example, Arc has no "Video Encode", so "Copy" is shown in its place.

---

## 6. Charts (`AreaChart` control)

It is a Win2D `CanvasControl` wrapped in a templated control with these properties:
`Series` (1–2 `RingBuffer<float>`), `Maximum` (fixed 100 or `Auto`), `StrokeBrush`, `FillOpacity` (≈0.15–0.2), `SecondaryDashed`, `ShowGrid`, `ShowAxisCaptions`, `Compact` (the summary-tile variant: no grid or captions and a thinner border).

**Rendering, matching Task Manager**
- A 1 px border in the accent colour.
- A light grid with 10 horizontal and 12 vertical cells. The grid **scrolls left** with the data, as in Task Manager, by offsetting the vertical lines by the sample phase.
- The series is drawn as a `CanvasGeometry` path, closed to the baseline, filled at low opacity and stroked with a 1 px line.
- The newest sample is on the right edge. Missing history (the first minute) is left empty on the left.
- An auto-scale maximum picks a "nice" ceiling (1-2-5 × 10ⁿ in bytes or bits) with hysteresis, so the axis does not flicker.

**Performance**
- Call `Invalidate()` only when a new snapshot arrives.
- Charts that are not visible are not updated. Tiles scrolled out of view are virtualized by the `ItemsRepeater`.
- Brushes and grid geometry are cached per size and theme.
- The per-core grid at 32–128 logical processors is the stress case. If it exceeds the frame budget, fall back to a single `CanvasControl` that draws every cell.

**Accent colours** (from Task Manager on Windows 11; defined in `Themes/Colors.xaml` for light and dark):
CPU `#117DBB` · Memory `#8B12AE` · Disk `#4DA60C` · Network `#A74F01` · GPU `#0B8579`

---

## 7. Details blocks

The details block below each focus chart copies Task Manager's two-part layout:
- **Left:** large live stats as label-over-value pairs in a wrapping panel, for example Utilization, Speed, Processes, Threads, Handles and Up time for CPU.
- **Right:** a two-column static key/value grid (for example Base speed, Sockets, Cores, Logical processors, Virtualization, L1/L2/L3 cache, Family/Model/Stepping).

One reusable `DetailsPanel` control takes `LiveStats` and `StaticInfo` collections of `(Label, Value)`. The block also has a right-click **Copy** command that copies all values as text.

---

## 8. Settings

The rail's settings button opens a `Flyout` anchored to it, containing:
- **Update speed**: `RadioButtons` for High (0.5 s), Normal (1 s, the default), Low (4 s) and Paused. A custom `NumberBox` (0.25–10 s) is optional.
- **Theme** (optional): System / Light / Dark.

A change applies immediately: the `Sampler` interval changes, and the ring buffers are kept. Settings persist in `ApplicationData.Current.LocalSettings`. Window size and position and the last focus item are also restored on launch.

---

## 9. Known platform constraints and risks

| Risk | Impact | Mitigation |
|---|---|---|
| **RAM timings** (CL-tRCD-tRP-tRAS) are only in SPD EEPROM, which needs SMBus access through a ring-0 driver | Can't show real timings as a standard user | Show speed, configured speed, voltage, part number, form factor and slots. Leave a "Timings: unavailable" row, or decode known JEDEC defaults from the part number (v2). |
| **GPUs behind an on-card PCIe switch** (e.g. Intel Arc) report the switch's internal link (Gen 1 x1). Windows exposes no link properties for the switch ports. | Can't show the real slot link | Detect a switch (more than one PCI bridge above the GPU) and label the link "internal link behind on-card PCIe switch" |
| **USB multi-bay bridges** (e.g. JMicron) report petabyte-scale disk sizes through every API | Wrong capacity | Treat sizes above 1 PiB as unknown, and show the volume total "(from volumes)" instead |
| **Wi-Fi SSID** on Windows 11 24H2+ requires location permission | SSID shows blank | Declare the `location` capability in the manifest. Show "SSID hidden — enable location access" with a link to `ms-settings:privacy-location`. |
| PDH instance names are localized and mangled | Counter lookups fail on non-English systems | Always use `PdhAddEnglishCounter`. Use wildcard instances and match on the parsed parts. |
| GPU engine PDH instances churn as processes start and exit | Spikes or gaps | Use wildcard counter arrays, re-read on every tick, and aggregate by LUID/engine. Never hold per-process counter handles. |
| WMI is slow on first call (hundreds of ms) | Startup jank | Keep WMI off the UI thread and do it only for static info. The shell appears first, and details fill in later. |
| Hybrid GPUs (iGPU + dGPU) and remote sessions | Missing or extra adapters | Key everything by LUID, and skip adapters that have no PDH engine instances. |
| Per-core grid on high-core-count CPUs | Frame drops | Single-canvas fallback (§6) |
| ARM64 | No x86 CPUID | Registry and WMI path for CPU identification |

---

## 10. Open questions for you

All four are answered:

1. **GPU summary metric:** the busiest engine, as Task Manager does.
2. **Disk focus:** per physical disk, with volumes rolled up into it.
3. **Network list:** physical adapters only.
4. **Distribution:** sideload only, for personal use. So the app is unpackaged and self-contained, with no MSIX.

---

## 11. Milestones

**M0 — Scaffold (½ day)**
- Create the WinUI 3 packaged app, the `Detective.Core` class library and the test project. Add CsWin32 (`NativeMethods.txt`), Win2D, CommunityToolkit.Mvvm and DI.
- Set up Mica, the custom title bar ("Detective" and the icon), and an empty shell with the rail and the two-row right pane with the 70% / 300 px rule.
- *Done when:* the app launches, the rail switches placeholder views, and resizing respects the height rules.

**M1 — Sampling core and CPU end-to-end (2 days)**
- Build `PdhQuery`, `RingBuffer`, `Sampler` and `Snapshot`.
- Build `CpuProvider` (overall and per-LP) and the `AreaChart` control (full and compact).
- Build `CpuView` with the overall and logical-processor modes, the CPU summary tile and the CPU details block.
- *Done when:* CPU utilization tracks Task Manager within ±3% side by side, and the per-core grid renders on this machine.

**M2 — Memory (1 day)**
- Build `MemoryProvider`, the usage chart, `MemoryCompositionBar`, the DIMM details from WMI and SMBIOS, and the tile.
- *Done when:* In use, Available and composition match Task Manager.

**M3 — Disks (1.5 days)**
- Volume enumeration, the volume → physical disk map, LogicalDisk/PhysicalDisk counters, and the two-chart view.
- `MSFT_PhysicalDisk` details, one tile per volume, and hot-plug rescan.
- *Done when:* copying a large file shows matching R/W rates, and plugging in a USB drive adds a tile.

**M4 — Networks (1.5 days)**
- `GetIfTable2` deltas, adapter filtering, IP/DNS, WLAN SSID with the location capability, type labels and hot-plug.
- *Done when:* a speed test shows matching throughput, and Wi-Fi and Ethernet both appear with correct details.

**M5 — GPUs (2 days)**
- DXGI enumeration, parsing and aggregation of the GPU Engine instances, the memory counters and budgets, and the PCIe link info through the device node.
- The four engine charts with engine pickers, the memory charts and the tiles.
- *Done when:* a game or video playback shows 3D and Video Decode activity matching Task Manager, and link speed and width match GPU-Z.

**M6 — Settings, selection sync and polish (1.5 days)**
- The settings flyout and persistence, and rail ↔ tile selection sync.
- Theme switching, keyboard navigation (arrow keys in the strip, Ctrl+1…5 for the rail), and a Narrator pass (`AutomationProperties` on charts announcing current values).
- Copy details, window state restore, and the app icon and branding.

**M7 — Hardening and release (1 day)**
- Profile Detective's own CPU and RAM, and run a 24 h soak test for leaks (PDH handles, Win2D resources).
- Error resilience: every provider catches its own errors and shows "—" instead of crashing.
- MSIX signing and a README.

Estimated total: **about 11 working days** for one developer.

---

## 12. Testing strategy

- **Unit (Detective.Core.Tests):** ring buffer, rate/delta maths (including counter wrap), GPU instance-name parser, `Processor Information` instance sorting, "nice" axis scaling, and memory composition arithmetic.
- **Snapshot mode (as built):** `Detective.exe --snapshot <dir> [--theme light|dark]` renders every focus view to PNG with `RenderTargetBitmap`, then exits. It works while the screen is locked or the window is covered. This is how the UI was verified.
- **Fake providers:** not built yet. The plan was `Fake*Provider` implementations behind the same interfaces, with a `--fake` switch.
- **Parity checks:** run side by side with Task Manager for each milestone's "done when" criteria.
- **Manual matrix:** light and dark theme, 100/150/200% DPI, a small window (the strip hits its 300 px cap versus the 30% rule), hot-plugging USB and Wi-Fi, and an iGPU+dGPU laptop if available.
