# E2E Test Infra: DASMO CYBER CAFE TOOLS Document Enhancement

## Test Philosophy
- Opaque-box, requirement-driven, deterministic image quality and performance validation.
- Methodology: Category-Partition + Boundary Value Analysis + Pairwise Combinatorial + Real-World Workload Testing.
- Zero-regression rule: All 76 baseline assertions in `SmartSaver.Tests` must remain 100% green at all times.

## Feature Inventory
| # | Feature | Source (requirement) | Tier 1 | Tier 2 | Tier 3 |
|---|---------|---------------------|:------:|:------:|:------:|
| 1 | Adaptive Background Whitening | ORIGINAL_REQUEST §R1 | 5 | 5 | ✓ |
| 2 | Shadow Gradient Removal | ORIGINAL_REQUEST §R1 | 5 | 5 | ✓ |
| 3 | Pitch-Black Text Deepening | ORIGINAL_REQUEST §R2 | 5 | 5 | ✓ |
| 4 | Anti-Aliased Stroke Continuity | ORIGINAL_REQUEST §R2 | 5 | 5 | ✓ |
| 5 | Color Stamp & Photo Protection | ORIGINAL_REQUEST §R1 | 5 | 5 | ✓ |
| 6 | Execution Performance (<600ms) | ORIGINAL_REQUEST §Acceptance | 5 | 5 | ✓ |
| 7 | Preset Scaling (Light/Balanced/Strong) | ORIGINAL_REQUEST §R3 | 5 | 5 | ✓ |

## Test Architecture
- **Test Runner Location**: `c:\CODING\coading\DASMO CYBER CAFE\src\SmartSaver.Tests`
- **Build / Run Command**:
  `c:\CODING\coading\DASMO CYBER CAFE\.dotnet\dotnet.exe build .\src\SmartSaver.Tests\SmartSaver.Tests.csproj -c Debug`
  `.\src\SmartSaver.Tests\bin\Debug\net8.0-windows10.0.17763.0\win-x64\SmartSaver.Tests.exe 2>&1`
- **Pass/Fail Semantics**: Process exit code `0` for success; non-zero if any assertion fails.
- **Image Metrics Evaluators**:
  - `BackgroundWhiteness`: $\mu_{\text{RGB}} \ge 250$ on background zones (>160 RGB originally).
  - `StrokeDarkness`: Core text pixels (<100 RGB) deepened to $\le 35$ RGB.
  - `StrokeContinuity`: 8-connected BFS component traversal preserves single connected path (ratio = 1.00).
  - `ColorPreservation`: Retained Saturation Ratio $\text{RSR} \ge 85\%$ on chromatic stamp/photo regions ($\Delta_{\text{chroma}} \ge 22$).
  - `A4ExecutionBenchmark`: Stopwatch 5-iteration median CPU time $< 600$ ms on $2480 \times 3508$ A4 300 DPI canvas.

## Real-World Application Scenarios (Tier 4)
| # | Scenario | Features Exercised | Complexity |
|---|----------|--------------------|------------|
| 1 | Dirty Flatbed Scanner ID Card | F1, F2, F3, F4, F5 | High |
| 2 | Mobile Camera Uneven Lighting Passbook | F1, F2, F3, F7 | High |
| 3 | Blue Ink Signed Form with Purple Notary Stamp | F1, F3, F4, F5, F7 | High |
| 4 | Color Passport Photo Identity Card | F1, F2, F5, F7 | High |
| 5 | Full A4 300 DPI Legal Document Xerox Prep | F1, F2, F3, F4, F6 | High |

## Coverage Thresholds
- Tier 1: $\ge 5$ test cases per feature (happy path isolation).
- Tier 2: $\ge 5$ boundary & corner cases per feature (extreme gradients, pitch-black vs faded, tiny stamps).
- Tier 3: Pairwise coverage across features and presets.
- Tier 4: $\ge 5$ realistic cyber cafe application scenarios.
- Acceptance Target: Baseline 76 assertions + new suite assertions pass with 0 failures.
