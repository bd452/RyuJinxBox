# Ryujinx Xbox Series S/X Port

## Engineering Implementation Document

---

## 1. Scope & Assumptions

### Target
- Xbox Series S / Series X
- Xbox Developer Mode
- UWP / GameCore-compatible environment

### Non-goals
- Retail-mode distribution
- Emulator feature redesign
- GPU correctness research
- Legal or distribution concerns

### Core Assumptions
- Existing Ryujinx codebase (C#)
- Vulkan renderer remains authoritative
- A Vulkan → D3D12 translation layer is usable on Xbox
- ARM64 → x64 dynarec remains enabled
- Performance parity with mid-range PC is not required initially

---

## 2. High-Level Architecture

```
+---------------------------+
| Ryujinx Core |
| ------------ |
| CPU          | GPU | Kernel | FS |
+-----------+---------------+
            |
            v
+---------------------------+
| Host Abstraction |
| ---------------- |
| Input            | Audio   | Timing |
| FS               | Threads | Window |
+-----------+---------------+
            |
            v
+---------------------------+
| Vulkan Renderer (SPIR-V)  |
+-----------+---------------+
            |
            v
+---------------------------+
| Vulkan→D3D12 Translation  |
|   (DXVK-class layer)      |
+-----------+---------------+
            |
            v
+---------------------------+
|   Xbox D3D12 / GameCore   |
+---------------------------+
```

**Key idea:** Ryujinx believes it is running on a normal Vulkan + Windows-like host. The Xbox-specific work is concentrated below and beside it.

---

## 3. Toolchain & Dependencies (Chosen)

### Language / Runtime
- **.NET 8**
  - Reason: modern runtime, better AOT/JIT control, long-term support
- **C#** (existing Ryujinx code)

### Build System
- **MSBuild**
- **Visual Studio 2022**
  - Xbox Dev Mode tooling support
  - UWP/GameCore project templates

### Xbox Platform SDK
- **Xbox GDK**
  - Input
  - Audio
  - D3D12 access
  - App lifecycle
  - UWP / GameCore App Model

### Graphics
- **Vulkan SDK** (SPIR-V support only)
- **DXVK** (or derivative)
  - Vulkan → D3D12
  - Shader pipeline translation
  - Pipeline cache handling
- **Direct3D 12** (Xbox)

### CPU / JIT
- Ryujinx ARM64 dynarec (existing)
- Executable memory via platform-approved APIs
- x64 codegen only

### Audio
- **XAudio2** (Xbox implementation)

### Input
- **Windows.Gaming.Input**
  - Xbox controller mapping
  - Haptics

### Storage
- `ApplicationData.LocalFolder`
- `ApplicationData.TemporaryFolder`

### Diagnostics
- **PIX for Xbox**
- **ETW tracing**
- Custom Ryujinx logging backend (Xbox-safe)

---

## 4. Repository & Project Layout

```
/Ryujinx
  /src
    /Ryujinx.Core
    /Ryujinx.Graphics.Vulkan
    /Ryujinx.Cpu
    /Ryujinx.HLE
    /Ryujinx.UI.Common
    /Ryujinx.Host.Xbox       <-- new
    /Ryujinx.Graphics.Xbox   <-- glue layer only
  /external
    /dxvk
  /build
    /xbox
```

### New Projects
- **Ryujinx.Host.Xbox**
  - Implements host interfaces
  - Owns lifecycle, input, audio, FS
- **Ryujinx.Graphics.Xbox**
  - Vulkan loader overrides
  - DXVK initialization
  - Pipeline cache routing

---

## 5. Phase-by-Phase Implementation

---

### Phase 1: Host Bring-Up

#### 5.1 App Lifecycle
- Replace desktop main loop with:
  - `OnActivated`
  - `OnSuspending`
  - `OnResuming`
- Emulator core runs inside a managed execution loop owned by Xbox app shell

#### 5.2 Filesystem Mapping

Map Switch components to sandbox-safe locations:

| Emulated Component | Xbox Path                     |
| ------------------ | ----------------------------- |
| NAND               | `LocalFolder/nand`            |
| SD Card            | `LocalFolder/sd`              |
| Shader Cache       | `LocalFolder/cache/shaders`   |
| Pipeline Cache     | `LocalFolder/cache/pipelines` |
| Logs               | `TemporaryFolder/logs`        |

No assumptions about:
- symlinks
- arbitrary absolute paths
- fast seek-heavy IO

---

### Phase 2: Input & Audio

#### Input
- Map Xbox controller → Switch Pro layout
- Handle:
  - analog deadzones
  - trigger normalization
  - rumble passthrough

#### Audio
- XAudio2 backend
- Low-latency buffer config
- Avoid large ring buffers (Switch games expect tight audio timing)

---

### Phase 3: GPU Stack Integration

#### 5.3 Vulkan Loader Control
- Replace Vulkan loader discovery with explicit DXVK bootstrap
- Disable implicit desktop Vulkan layers

#### 5.4 DXVK Configuration
- D3D12 backend only
- Pipeline cache enabled
- Aggressive pipeline reuse
- Explicit shader cache directory mapping

#### 5.5 Shader Pipeline Discipline
- Reduce pipeline permutations
- Trim unused specialization constants
- Cap cache growth with LRU eviction

**This phase determines whether games feel playable or stutter catastrophically.**

---

### Phase 4: CPU Dynarec Integration

#### 5.6 Executable Memory
- Allocate code cache via platform-approved APIs
- Enforce:
  - W^X discipline
  - predictable invalidation

#### 5.7 Exception & Signal Emulation
- Replace POSIX signal assumptions
- Map faults → structured exception handling
- Preserve fast-path dispatch

No interpreter fallback unless strictly necessary.

---

### Phase 5: Memory & Stability

#### 5.8 Virtual Memory Strategy
- Reduce maximum reserved ranges
- Compress page tables
- Prefer sparse mappings over monolithic reservations

#### 5.9 Cache Auditing

Aggressively profile:
- JIT code cache size
- Shader cache growth
- Texture residency

Target:
- Stable peak memory
- Predictable allocation curves

---

### Phase 6: Timing & Performance

#### 5.10 Scheduler Tuning
- Thread priorities:
  - CPU emulation
  - GPU submission
  - IO
- Reduce unnecessary sync points

#### 5.11 Frame Pacing
- Align emulator vsync with Xbox display timing
- Avoid host-induced jitter

---

## 6. Series S vs Series X Considerations

| Area       | Series S         | Series X         |
| ---------- | ---------------- | ---------------- |
| GPU        | Main bottleneck  | CPU more visible |
| Memory     | Tighter          | More slack       |
| Resolution | Prefer 720p–900p | 1080p feasible   |

Expect separate tuning profiles.

---

## 7. Testing Strategy

### Smoke
- Boot firmware
- Homebrew
- Menu rendering

### Functional
- 2D titles
- Low-shader-count 3D titles

### Stress
- Shader-heavy scenes
- Rapid scene transitions
- Suspend/resume cycles

---

## 8. Risk Summary (Engineering Only)

| Risk                  | Nature      |
| --------------------- | ----------- |
| Shader compile stalls | Performance |
| Pipeline cache bloat  | Memory      |
| JIT sandbox limits    | Platform    |
| Thread starvation     | Timing      |

None are unsolved problems; all are tuning-heavy.

---

## 9. Final Assessment

With:
- Vulkan preserved
- Translation layer in place
- Dynarec intact

This port is:
- **Architecturally clean**
- **Engineering-heavy but finite**
- **Comparable to a console engine port, not emulator R&D**
