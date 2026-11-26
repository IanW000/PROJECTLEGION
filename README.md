# PROJECTLEGION

## Windows x64 移动基准

使用 Unity 6000.0.66f2 和项目锁定的包版本。在 **File → Build Profiles** 选择 Windows x64 作为活动平台，然后用 **Tools → Project Legion → Build Windows Benchmark Player** 生成 `LegionBenchmark.exe`。菜单显式构建 `MovementBenchmark.unity` 和 Development Player，保留项目的启用场景列表。所有模式使用同一份 Player、质量配置和启动参数。命令行构建可在隔离项目副本上使用 `-batchmode -quit -buildTarget Win64 -executeMethod ProjectLegion.MovementBenchmark.BenchmarkWindowsBuild.BuildFromCommandLine -legion-build-path <exe路径>`。

从仓库根目录运行。以下 PowerShell 示例将输出写到独立目录，并捕获 Player 退出码。

```powershell
$exe = 'C:\Bench\LegionBenchmark.exe'
$gitCommit = (git rev-parse HEAD).Trim()
$results = 'C:\Bench\Results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$playerArgs = @(
    '-batchmode', '-nographics',
    '-legion-benchmark', 'single', '-legion-mode', 'E', '-legion-units', '1000',
    '-legion-output', ('"' + $results + '"'),
    '-legion-git', $gitCommit,
    '-logFile', ('"' + (Join-Path $results 'player.log') + '"')
)
$player = Start-Process -FilePath $exe -ArgumentList $playerArgs -PassThru -Wait
$player.ExitCode
```

将 `single` 后的模式和单位数选项替换为 `suite -legion-repetitions 3 -legion-order-seed 71237` 即可启动全套测量。A 为 MonoBehaviour，B/C 为同步 ECS，D/E 为并行 ECS，C/E 使用 Burst。单位数支持 100、500、1,000、2,500、5,000、10,000。全套默认先执行各模式的独立零单位基线，再执行三轮。每轮以固定种子打乱 30 个组合，共 90 个非零测量运行。单次命令默认执行一组零单位基线和一次指定组合。Git SHA 可省略，带本地改动的构建应另记差异。

正式运行固定步长为 1/60 秒，每帧一步，300 步预热后记录 1,000 步。冒烟可在 `single` 追加 `-legion-smoke`，改用 2 步预热和 6 步采样，导出明确标记为冒烟。Player 只在两次运行之间写 CSV 和报告进度。日志输出本次运行目录。要取消，在该目录新建空文件 `cancel.request`，Player 会在收到系统文件通知后安全结束当前运行，并保留已完成的结果。文件通知异步送达，极短的运行可能先结束。退出码 0 表示完成，1 表示失败，2 表示取消。

### CSV 与计时口径

| 文件 | 内容 |
|---|---|
| `frames.csv` | 每个已验证运行的逐帧样本，包含运行序号、模式、单位数、重复序号、基线标记及步号 |
| `summary.csv` | 每次运行、每项指标的有效样本数、最小值、最大值、均值、中位数、总体标准差、P95、P99 和适用的吞吐量 |
| `runs.csv` | 尝试过的运行状态、样本数和详情。读取正式样本时按此表筛选 `Completed` |
| `environment.csv` | Unity、平台、构建、脚本后端、Burst 探针、硬件、worker 数、计时器和工作负载配置 |

- `step_wall_ms` 从早期 Update 开步计到帧末回调完成，包含窗口内的其他主线程活动。 `frame_wall_ms` 是相邻两次早期 Update 的墙钟间隔，上一帧记录在下一次 Update 补齐，包含引擎开销、等待和探针开销。
- B/C 的 `sync_dispatch_wall_ms` 包含 ECS 系统更新和同步作业。D/E 的 `schedule_dispatch_wall_ms` 包含系统更新、调度和派发，`complete_wall_ms` 包含完成依赖时的等待及调用开销。单独的 worker 执行时长和线程 CPU 时间留为 `N/A`。
- `main_thread_profiler_ms` 使用 Unity 的 `Main Thread` 标记。GC 分配与总使用／保留内存使用 Unity 的字节计数器。汇入前检查名称、类别、单位和该帧是否出现新样本。无法确认的值写为 `N/A`。内存值是引擎级观测，覆盖整个进程的 Unity 工作负载。
- 百分位按排序后的零基秩 `(n - 1) × p` 在线性相邻样本之间插值。总体标准差的分母为有效样本数。吞吐量为单位数 × 样本数 ÷ 对应时长总和（秒），分别针对开步窗口和完整帧计算。零单位基线单独导出，原始测量值保留。

每次运行在采样结束后按稳定索引与相同种子生成的托管参考比较。位置容差为默认场地最大坐标量级的一个 float ULP，速度精确比较。诊断作业在启动时独立执行，计时窗口内使用常规实体 archetype。

