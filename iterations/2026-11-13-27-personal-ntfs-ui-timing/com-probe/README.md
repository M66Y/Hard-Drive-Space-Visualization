# COM 启动线程最小复现

源文件以 `Program.cs.txt` 保存，防止进入主项目默认编译。探针只初始化 COM，不打开卷、不使用 VSS、不提权。每个独立进程的入口为 STA 或 MTA，启动同样的 MTA 工作线程后执行 `Thread.Join`；工作线程先等待 100 ms，随后调用与扫描来源相同参数的 `CoInitializeEx`、`CoInitializeSecurity`。

在 .NET 10.0.14 / SDK 10.0.403 上实际观察 12 个独立进程：带 apartment 查询的两组各 3 次，取消查询的两组各 3 次。6 次 STA 均返回 `0x80010119`（`RPC_E_TOO_LATE`），6 次 MTA 均返回 `S_OK`。原始固定字段观察分别见 `results.json` 和 `bare-results.json`，没有路径、进程身份或任意异常消息。它证明启动线程机制的复现，不能替代旧生产失败的具体 HRESULT；旧生产只取得 `SnapshotFailed`。

复现时在新的临时目录复制此源并改名 `Program.cs`，建立 `net10.0-windows` 控制台项目，依次将 `StartupObject` 设为 `ComProbe.StaEntry`、`ComProbe.MtaEntry`、`ComProbe.BareStaEntry`、`ComProbe.BareMtaEntry`。每种独立构建后运行三次新进程，不在同一进程重试安全初始化。当时实际使用安装 SDK 的 Roslyn 编译器和 net10.0 引用包直接编译，没有还原新依赖；带固定本机 SDK 路径的临时启动脚本未归档为通用工具。

机制依据：[Thread.Join 会进行标准 COM 消息泵处理](https://learn.microsoft.com/en-us/dotnet/api/system.threading.thread.join?view=net-10.0)；[CoInitializeSecurity 每进程只初始化一次，自动初始化后显式调用会返回 RPC_E_TOO_LATE](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coinitializesecurity)。生产修复保留安全检查，将进程入口设为 MTA，桌面、剪贴板及文件夹对话框仍运行在显式 STA 线程。
