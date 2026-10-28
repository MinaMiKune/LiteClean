# LiteClean 性能测试记录

## 1. 环境

| 项目 | 值 |
|---|---|
| 测试时间 | 2026-10-28（Asia/Hong_Kong） |
| 操作系统 | Windows 11，NT 10.0.26200.0，x64 |
| 运行时 | .NET 10.0.12 |
| SDK | .NET SDK 10.0.401 |
| CPU | 6 个逻辑处理器 |
| 进程权限 | 非管理员（`IsInRole(Administrator)=False`） |
| 磁盘 | 系统临时目录所在本地固定磁盘（环境未提供独立的磁盘型号/介质信息） |
| 构建 | Release，`PlatformTarget=x64` |
| 测试工具 | `tests/LiteClean.Tests` 内置 benchmark（无第三方依赖） |

## 2. 方法

运行：

```powershell
.\scripts\benchmark.ps1
# 或更大规模：
.\scripts\benchmark.ps1 --files 20000 --big 40
```

benchmark 在 `%TEMP%\LiteClean.Tests\<guid>\root` 下自动创建隔离目录树：

- 500 个普通目录，每目录若干 512 B 文件；
- 一个宽目录（大量 512 B 文件）；
- 一条 120 层深目录，末端 5 个文件；
- 若干 1 MiB 文件；
- 所有文件时间戳设为未来 24 小时保护窗口之外；
- 所有数据均在自动创建的测试目录内，测试结束后删除。

测试流程：

1. 生成数据后执行 GC；
2. 使用 `TempScanner` 扫描全部内容；
3. 使用 `TempCleaner` 清理全部候选；
4. 校验扫描计数、字节数与清理后的文件系统状态；
5. 记录 `Stopwatch` 耗时、`Process.WorkingSet64`、`Process.PeakWorkingSet64` 和 `GC.GetTotalMemory`。

**注意**：`PeakWorkingSet64` 是同一进程整个生命周期（包括测试数据生成）的峰值，因此是偏保守的上界；managed heap 数据是扫描后的即时值。没有进行预热或多轮取平均值。耗时和吞吐会受磁盘状态、杀毒软件、系统负载影响，不能外推为所有机器上的固定指标。

## 3. 默认规模结果（6030 个文件 / 623 个目录 / 29,288,960 字节）

最终运行：

| 指标 | 扫描 | 清理 |
|---|---:|---:|
| 耗时 | 1021 ms | 3582 ms |
| 处理文件数 | 6030 | 6030 |
| 处理目录数 | 623 | 623 |
| 文件吞吐 | 约 5,904 文件/秒 | 约 1,684 文件/秒 |
| 逻辑字节 | 29,288,960 | 29,288,960 |
| 跳过 | 0 | 0 |
| 失败 | — | 0 |

内存：

| 指标 | 值 |
|---|---:|
| 扫描前工作集 | 36.9 MB |
| 扫描后工作集 | 43.7 MB |
| 进程生命周期峰值工作集（含数据生成） | 46.7 MB |
| 扫描后 managed heap | 5.4 MB |
| 清理后工作集 | 45.5 MB |

同一环境下此前一次运行：扫描 1122 ms、5,373 文件/秒；清理 3756 ms、1,606 文件/秒；峰值 46.2 MB。说明存在约 5–10% 的运行间波动。

原始输出：`artifacts/benchmark-results.txt`。

## 4. 压力规模结果（20,045 个文件 / 623 个目录 / 52,185,600 字节）

其中宽目录约 17,000 个文件，深目录 120 层。

| 指标 | 扫描 | 清理 |
|---|---:|---:|
| 耗时 | 4312 ms | 12,393 ms |
| 文件吞吐 | 约 4,649 文件/秒 | 约 1,617 文件/秒 |
| 跳过的文件 | 0 | 0 |
| 失败 | — | 0 |
| 工作集（处理后的即时值） | 53.7 MB | 62.3 MB |
| 进程生命周期峰值（含 22.7 s 数据生成） | 55.8 MB | 62.3 MB |
| managed heap | 10.5 MB | — |
| 剩余文件 | 0 | 0 |

原始输出：`artifacts/benchmark-results-stress.txt`。

## 5. 功能测试中的压力用例

- `ScanTests.WideDirectoryIsFullyEnumerated`：800 个文件的宽目录全部枚举，计数和字节精确；
- `ScanTests.UnicodeAndLongPathsAreScanned`：路径超过 260 字符，正常扫描；
- `CleanTests.UnicodeSpacesAndLongNamesAreCleaned`：超过 260 字符路径与 Unicode/空格名称正常删除；
- `CancellationTests.ScanCanBeCanceled`：1500 个文件扫描期间取消，未修改文件；
- `CleanTests.CancellationStopsNewDeletes`：200 个文件，在约 5 项后取消，验证未继续发起删除；
- 目录联接：真实创建 junction 并验证不跟随、不逃逸；
- 20,000 文件 benchmark：扫描/清理完整成功，统计一致。

## 6. 结论与限制

在这些实测数据中：

- 扫描约数百万字节/秒到约 6000 文件/秒的数量级，主要开销是每文件的独占打开、身份读取和最终路径校验（这些是安全换来的成本）；
- 清理约 1600 文件/秒，主要开销是逐文件独占打开、身份/时间/大小校验、disposition 和关闭后确认；
- 20k 文件的 managed heap 约 10.5 MB，进程峰值约 62 MB；
- 内存随候选数量增长（为支持逐项选择保留候选记录），不是固定值；
- 该数据不能代表网络盘、机械硬盘、杀毒软件全盘扫描、EFS/云占位文件等场景。

## 7. 复现命令与环境记录

```powershell
# 构建
.\scripts\build.ps1

# 默认 benchmark
.\scripts\benchmark.ps1

# 20k 文件压力 benchmark
.\scripts\benchmark.ps1 --files 20000 --big 40

# 查看原始输出
Get-Content .\artifacts\benchmark-results.txt
Get-Content .\artifacts\benchmark-results-stress.txt
```

本次实测没有使用模拟扫描结果或人为填写的性能数字；上述数字来自 `Stopwatch`、`Process` 和 `GC` 的真实读数。
