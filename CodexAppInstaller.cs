// CodexAppInstaller — Codex App（ChatGPT Desktop，MSIX）一键安装（国内网络优化版，纯 C#）
// 功能：
//   1. 从镜像仓库（GitHub + gh-proxy 双前缀）下载官方 Store 签名 MSIX，SHA256 校验 + 下载缓存
//   2. Add-AppxProvisionedPackage（机器范围）→ 用户级立即注册（复用 Claude 版修复逻辑）
//   3. sub2api 自动登录（步骤 1）→ 自动创建/复用 API Key「codex桌面端」（步骤 3-4）
//      → 按 sub2api 官方模板写入 ~/.codex/config.toml：搜索=live、Full Access、记忆=开、
//         启动更新检查=关（步骤 5-8、10）
//   4. 从国内线路（gh-proxy）下载 codex-zh-CN 汉化包并自动汉化（步骤 9），
//      桌面快捷方式直接指向汉化补丁副本（不使用启动脚本）
//   5. 桌面快捷方式（Get-StartApps 动态 AUMID，原版回退）
// 编译：build-exe.bat（本机 .NET Framework csc + PowerShell SDK，无第三方依赖）
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Web.Script.Serialization;

// 程序集品牌信息（文件属性 / 资源管理器显示）
using System.Reflection;
[assembly: AssemblyTitle("Xode AI 安装程序")]
[assembly: AssemblyProduct("Xode AI — Codex App 一键安装")]
[assembly: AssemblyCompany("Xode AI")]
[assembly: AssemblyCopyright("Copyright © Xode AI")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace CodexAppInstaller
{
    internal static class Program
    {
        private static bool SkipChecksum, SkipShortcut, SkipApi, SkipZhCn, TestSub2api, NoElevate, ShowHelp, PanelProbe;
        private static string ApiBaseUrl = "", ApiKey = "", ApiModel = "";
        private static string Site = "", Email = "", Password = "", LogFile = "", XodeLogoParam = "";
        private static string XodeLogoIcoPath = null;
        private static bool XodeBrandingOk;
        private const string MirrorRepo = "Wangnov/codex-app-mirror";
        // sub2api 站点与 Key 约定（站点固定在代码里，运行时不询问；-Site 可覆盖）
        private const string Sub2apiSiteDefault = "https://hub.xodeai.com";
        private const string Sub2apiKeyName = "codex桌面端";
        // 模型 provider 的显示名（应用左下角展示）
        private const string ProviderDisplayName = "Xode AI";
        // 无订阅时 Key 绑定的默认分组（名称精确匹配优先，其次包含关键字；不再询问用户）
        private const string DefaultGroupName = "GPT按量计费";
        private const string DefaultGroupNameKeyword = "按量计费";
        private const string DefaultApiBaseUrl = Sub2apiSiteDefault + "/v1";
        private const string DefaultModel = "gpt-6.1-sol";
        private const string CodexClientVersion = "0.158.0";   // /v1/models 模型目录需要的 client_version
        // codex-zh-CN 汉化包（GitHub Releases，经 gh-proxy 国内线路下载）——旧架构（GUI 为 Codex.exe）用
        private const string ZhCnRepo = "xqnode/codex-zh-CN";
        private const string ZhCnPinTag = "v0.1.2";
        // 汉化启动器（shibaweidu/codex-desktop-zh）——新架构（GUI 已改名 ChatGPT.exe）用：
        // 运行时注入官方 i18n，不改官方文件；菜单汉化需经启动器启动，webview 语言经 localeOverride 持久化
        private const string ZhLauncherRepo = "shibaweidu/codex-desktop-zh";
        private const string ZhLauncherAsset = "Codex-Zh-Launcher-Windows-x64.exe";
        private const string ZhLauncherPinTag = "v0.7.7";
        // 汉化脚本需要 Node.js 运行时；未安装时从 npmmirror 国内镜像下载便携版
        private const string NodeVersion = "v22.14.0";
        private static readonly string[] GhProxyPrefixes = {
            "https://v4.gh-proxy.org/https://github.com",
            "https://gh-proxy.org/https://github.com"
        };
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

        private static string CacheDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAppInstaller", "cache"); }
        }

        private class AppxInfo
        {
            public string Name, Version, PackageFamilyName, InstallLocation, PackageFullName;
        }

        // 控制台 + 文件双写（-LogFile）
        private class TeeWriter : TextWriter
        {
            private readonly TextWriter _out;
            private readonly string _path;
            private readonly object _lock = new object();
            public TeeWriter(TextWriter console, string path) { _out = console; _path = path; }
            public override Encoding Encoding { get { return _out.Encoding; } }
            public override void Write(char value) { _out.Write(value); Append(value.ToString()); }
            public override void Write(string value) { _out.Write(value); Append(value); }
            public override void WriteLine(string value) { _out.WriteLine(value); Append(value + Environment.NewLine); }
            private void Append(string s)
            {
                try { lock (_lock) File.AppendAllText(_path, s, Encoding.UTF8); }
                catch { }
            }
        }

        // ================= 右侧品牌信息面板（Xode AI 平台 / 复制网址注册 / 客服二维码） =================
        // 踩坑记录（改布局前必读）：
        //  - 块字符 ▀ 必须 UTF-8 输出：Main 最前已设 OutputEncoding=UTF8（GBK 不含块字符，写出变 '?'）
        //  - ANSI 真彩需在 stdout 句柄开 ENABLE_VIRTUAL_TERMINAL_PROCESSING(0x0004)，否则转义码原样打印
        //  - 字体须切 Consolas 8x16：默认位图字体半块空心、QR 不可扫；6px 会 ClearType 彩条纹，8px 干净
        //  - 象限字符（▘▝▖▗等）经字体回退渲染成空心轮廓，禁用；半块 ▀（1 字符列 2 模块行）是渲染上限
        //  - 面板绘制只走 _raw（裸控制台），绝不能经 Console.Write（Console.Out 已是本类，会递归）
        //  - 窗口缩放三步：缩窗 → 放 buffer → 开窗（buffer 直接缩到小于当前窗口宽会抛异常）
        //  - QR 矩阵预生成（qrcode 库 v3-L，37x37 含 4 模块静区），源图 img/kefu-qr-src.jpg，
        //    内容 https://work.weixin.qq.com/u/vc84875590e6bf20e2（已裁 ?v=&bb= 跟踪参数，302 行为一致）
        private static TextWriter _consoleReal;  // 裸控制台输出（任何包装之前捕获，供面板绘制）
        private static PanelUi _panel;

        private static readonly string[] KefuQrMatrix = new string[]
        {
            "0000000000000000000000000000000000000",
            "0000000000000000000000000000000000000",
            "0000000000000000000000000000000000000",
            "0000000000000000000000000000000000000",
            "0000111111100100001001110011111110000",
            "0000100000101101101111001010000010000",
            "0000101110100111001110100010111010000",
            "0000101110101101010111001010111010000",
            "0000101110100010111000111010111010000",
            "0000100000101010000111101010000010000",
            "0000111111101010101010101011111110000",
            "0000000000000000101010000000000000000",
            "0000111110111110110111001101010100000",
            "0000110100011100001001111110100010000",
            "0000001011100101100101000011000000000",
            "0000010110011111000100011110010100000",
            "0000011101100101111011010000011000000",
            "0000110110011110100001110011100010000",
            "0000101100101000101100000000011000000",
            "0000101111000001101110011010100100000",
            "0000001001101000010111110001011000000",
            "0000111000011011101001111111101010000",
            "0000100100101010011101001001001000000",
            "0000100100000110000100000001000100000",
            "0000101001110010111011011111101110000",
            "0000000000001001000000001000111110000",
            "0000111111101010110110011010111000000",
            "0000100000100100100000011000100010000",
            "0000101110101100011101101111101010000",
            "0000101110101011100011100000011110000",
            "0000101110101110111101001111111100000",
            "0000100000101011100010011111010100000",
            "0000111111101110010111010001111000000",
            "0000000000000000000000000000000000000",
            "0000000000000000000000000000000000000",
            "0000000000000000000000000000000000000",
            "0000000000000000000000000000000000000"
        };

        // 面板 Writer：Console.Out 换成本类。左侧内容按列宽换行（不越面板），每次换行后重画面板
        // （buffer 高==窗口高，内容到底滚动会带动面板行，需整面重绘覆盖）。
        private sealed class PanelUi : TextWriter
        {
            private readonly object _sync = new object();
            private readonly TextWriter _log;   // 左侧内容出口（Tee 或裸控制台；进日志）
            private readonly TextWriter _raw;   // 裸控制台（面板绘制专用，不进日志）
            private const int PanelW = 52;      // 面板总宽（含边框）
            private const int QrModules = 37;   // QR 边长（含 4 模块静区；半块渲染 1 字符列 = 1 模块列）
            private const int QrCellRows = 19;  // 半块渲染：37 模块 → 19 字符行高
            private const int ContentH = 26;    // 面板内容总行数（品牌+间距+标签+网址+间距+文案+QR19+尾距）
            private int _col;
            private readonly StringBuilder _chunk = new StringBuilder();
            private int _lastViewTop = -1, _lastW = -1, _lastH = -1;   // 重绘节流：视口行/尺寸缓存

            private static string At(int row, int col) { return "\x1b[" + (row + 1) + ";" + (col + 1) + "H"; }
            private const string Reset = "\x1b[0m";

            public PanelUi(TextWriter log, TextWriter raw) { _log = log; _raw = raw; }
            public override Encoding Encoding { get { return _log.Encoding; } }

            private static void Diag(string s) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "panel_diag.log"), s + "\r\n"); } catch { } }

            public static PanelUi TryCreate(TextWriter raw)
            {
                try
                {
                    if (Console.IsOutputRedirected || GetConsoleWindow() == IntPtr.Zero) return null;
                    PanelUi p = new PanelUi(Console.Out, raw);
                    if (!p.Setup()) { Diag("fail: setup"); return null; }
                    Console.SetOut(p);
                    p.Draw(true);
                    // resize 竞态兜底：部分终端（实测客户 WT）窗口重排滞后于 API 返回，首帧可能错位
                    // 且初始阶段无滚动触发重画——稳定后后台补画一帧自愈
                    System.Threading.Thread selfHeal = new System.Threading.Thread(delegate()
                    {
                        System.Threading.Thread.Sleep(500);
                        try { p.Draw(true); } catch { }
                    });
                    selfHeal.IsBackground = true;
                    selfHeal.Start();
                    return p;
                }
                catch (Exception ex) { Diag("fail: ex " + ex.GetType().Name + " " + ex.Message); return null; }
            }

            private bool Setup()
            {
                IntPtr h = GetStdHandle(StdOutputHandle);
                uint mode = 0;
                if (GetConsoleMode(h, out mode)) SetConsoleMode(h, mode | 0x0004);   // ANSI VT
                try
                {
                    CONSOLE_FONT_INFO_EX f = new CONSOLE_FONT_INFO_EX();
                    f.cbSize = Marshal.SizeOf(typeof(CONSOLE_FONT_INFO_EX));
                    f.dwFontSize = new COORD(8, 16);
                    f.FontFamily = 54;
                    f.FontWeight = 400;
                    f.FaceName = "Consolas";
                    SetCurrentConsoleFontEx(h, false, ref f);
                }
                catch { }
                // 屏幕自适应：WT 记忆窗口尺寸/屏幕钳制/行高 1.2 等用户设置（客户机 2160×1440@150% +
                // 行高 1.2 实测）都会使固定目标 124×44 超出屏幕——WT 把窗口钳在屏内只显示顶部几十行，
                // 而 conhost 的 srWindow 仍报告请求值 → 面板按请求布局整体错位。resize 之前先按初始
                // 视口实测 cell 像素，算出屏幕可容纳的行列数，把目标收敛进去
                int targetW = 124, targetH = 44;
                try
                {
                    IntPtr hwnd = GetConsoleWindow();
                    RECT wr = new RECT(), cr = new RECT();
                    CONSOLE_SCREEN_BUFFER_INFO info0;
                    if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, ref wr) && GetClientRect(hwnd, ref cr)
                        && GetConsoleScreenBufferInfo(h, out info0))
                    {
                        int rows0 = info0.srWindow.B - info0.srWindow.T + 1;
                        int cols0 = info0.srWindow.R - info0.srWindow.L + 1;
                        int clientH = cr.Bottom - cr.Top, clientW = cr.Right - cr.Left;
                        if (rows0 > 0 && cols0 > 0 && clientH >= rows0 && clientW >= cols0)
                        {
                            int lineH = clientH / rows0, cellW = clientW / cols0;   // 实测 cell 像素（初始视口可信）
                            int chromeH = (wr.Bottom - wr.Top) - clientH, chromeW = (wr.Right - wr.Left) - clientW;
                            int maxRows = (GetSystemMetrics(SM_CYFULLSCREEN) - chromeH) / lineH;
                            int maxCols = (GetSystemMetrics(SM_CXFULLSCREEN) - chromeW) / cellW;
                            if (maxRows > 0) targetH = Math.Min(targetH, maxRows);
                            if (maxCols > 0) targetW = Math.Min(targetW, maxCols);
                        }
                    }
                }
                catch { }
                ResizeWindow(h, targetW, targetH);
                SyncViewport(h);
                DiagSnapshot(h, "setup");
                int w, hh;
                GetSize(out w, out hh);
                return w >= 92 && hh >= 28;   // 内容最小 26 行 + 上下边框；宽度需容纳面板 52 列 + 左区可读
            }

            private static void ResizeWindow(IntPtr h, int w, int hh)
            {
                try
                {
                    CONSOLE_SCREEN_BUFFER_INFO info;
                    if (!GetConsoleScreenBufferInfo(h, out info)) { Diag("resize: no info"); return; }
                    // 注意勿按 dwMaximumWindowSize 收敛：WT 会把它报成当前 buffer 宽度，扩宽永远失败；
                    // 直接以目标尺寸调用，失败（屏幕过小等）则保持原几何，Draw 按实际视口自适应
                    SMALL_RECT r = new SMALL_RECT();
                    r.L = 0; r.T = 0;
                    r.R = (short)Math.Max(10, Math.Min(w, info.srWindow.R - info.srWindow.L + 1) - 1);
                    r.B = (short)Math.Max(5, Math.Min(hh, info.srWindow.B - info.srWindow.T + 1) - 1);
                    // WT 下 SetConsoleWindowInfo 会失败（视口由终端接管）——失败不放弃，仍尝试 buffer 扩容
                    SetConsoleWindowInfo(h, true, ref r);
                    SetConsoleScreenBufferSize(h, new COORD((short)w, (short)hh));
                    SMALL_RECT full = new SMALL_RECT();
                    full.L = 0; full.T = 0; full.R = (short)(w - 1); full.B = (short)(hh - 1);
                    SetConsoleWindowInfo(h, true, ref full);
                }
                catch { }
            }

            private static void GetSize(out int w, out int h)
            {
                try { w = Console.WindowWidth; h = Console.WindowHeight; }
                catch { w = 124; h = 44; }
            }

            // 视口统一：buffer 与视口尺寸不一致（scrollback 分裂态）或视口未顶格（T≠0）时，
            // VT 定位（视口相对）与 WriteConsoleOutputW（buffer 绝对）两套坐标会错位——
            // 把视口顶回 0、buffer 收缩到视口尺寸；失败则 Draw 按各自语义修正坐标兜底。
            // 某些终端（客户机 WT）下这些调用可能滞后生效，故重读验证 + 多轮重试直到稳定
            private static void SyncViewport(IntPtr h)
            {
                for (int round = 0; round < 3; round++)
                {
                    CONSOLE_SCREEN_BUFFER_INFO info;
                    if (!GetConsoleScreenBufferInfo(h, out info)) return;
                    int W = info.srWindow.R - info.srWindow.L + 1;
                    int H = info.srWindow.B - info.srWindow.T + 1;
                    bool moved = info.srWindow.T != 0 || info.srWindow.L != 0;
                    bool split = info.dwSize.X != W || info.dwSize.Y != H;
                    if (!moved && !split) return;
                    try
                    {
                        SMALL_RECT small = new SMALL_RECT();
                        small.L = 0; small.T = 0;
                        small.R = (short)Math.Max(10, Math.Min(W, info.dwSize.X) - 1);
                        small.B = (short)Math.Max(5, Math.Min(H, info.dwSize.Y) - 1);
                        SetConsoleWindowInfo(h, true, ref small);
                        if (split) SetConsoleScreenBufferSize(h, new COORD((short)W, (short)H));
                        SMALL_RECT full = new SMALL_RECT();
                        full.L = 0; full.T = 0; full.R = (short)(W - 1); full.B = (short)(H - 1);
                        SetConsoleWindowInfo(h, true, ref full);
                    }
                    catch { }
                    System.Threading.Thread.Sleep(15);   // 给终端桥接一点生效时间
                }
            }

            // 面板状态日志（追加式）：客户机上若渲染异常，回传 %TEMP%\xode_panel_info.txt 全序列即可定位
            private static void DiagSnapshot(IntPtr h, string tag)
            {
                try
                {
                    CONSOLE_SCREEN_BUFFER_INFO info;
                    if (!GetConsoleScreenBufferInfo(h, out info)) return;
                    string s = string.Format(
                        "{0} {1}: buffer={2}x{3} window={4}x{5} viewTop={6} cursor=({7},{8}) max={9}x{10}\r\n",
                        DateTime.Now.ToString("MM-dd HH:mm:ss"), tag,
                        info.dwSize.X, info.dwSize.Y,
                        info.srWindow.R - info.srWindow.L + 1, info.srWindow.B - info.srWindow.T + 1,
                        info.srWindow.T, info.dwCursorPosition.X, info.dwCursorPosition.Y,
                        info.dwMaximumWindowSize.X, info.dwMaximumWindowSize.Y);
                    string p = Path.Combine(Path.GetTempPath(), "xode_panel_info.txt");
                    if (File.Exists(p) && new FileInfo(p).Length > 256 * 1024) File.Delete(p);
                    File.AppendAllText(p, s);
                }
                catch { }
            }

            // 远程排障：几何探测标尺。同一行号打印两套标记——N##（.NET SetCursorPosition 定位）
            // 与 V##（VT CUP 定位），客户截图回传后即可读出真实行数/行高/两套坐标系的映射偏差
            public static void Probe(PanelUi panel, TextWriter raw)
            {
                try
                {
                    IntPtr h = GetStdHandle(StdOutputHandle);
                    SyncViewport(h);
                    DiagSnapshot(h, "probe");
                    int w = 0, hgt = 0;
                    try { w = Console.WindowWidth; hgt = Console.WindowHeight; } catch { }
                    // VT 标尺（黄）：行 0,2,4.. 于列 8；列标尺于最后一行
                    for (int r = 0; r < hgt; r += 2)
                        raw.Write(At(r, 8) + "\x1b[93mV" + r.ToString().PadLeft(2, '0') + Reset);
                    for (int c = 0; c < w - 6; c += 10)
                        raw.Write(At(Math.Max(0, hgt - 1), c) + "\x1b[93m|" + (c % 100).ToString("D2") + Reset);
                    raw.Write(At(Math.Max(0, hgt - 1), w - 14) + "\x1b[91mEND " + w + "x" + hgt + Reset);
                    // .NET 标尺（青）：临时换回原始输出绕过面板，SetCursorPosition 定位
                    TextWriter old = Console.Out;
                    try
                    {
                        Console.SetOut(raw);
                        for (int r = 0; r < hgt; r += 2)
                        {
                            Console.SetCursorPosition(14, r);
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.Write("N" + r.ToString().PadLeft(2, '0'));
                            Console.ResetColor();
                        }
                        Console.SetCursorPosition(Math.Max(0, w - 6), Math.Max(0, hgt - 1));
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write("NET");
                        Console.ResetColor();
                    }
                    finally { Console.SetOut(old); }
                }
                catch { }
            }

            private int PanelLeft(int width) { int bw = Math.Min(PanelW, width - 1); return width - bw; }
            private int LeftWidth() { int w, h; GetSize(out w, out h); return Math.Max(24, PanelLeft(w) - 2); }

            private static int CharWidth(char c)
            {
                if ((c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0x303E) || (c >= 0x3041 && c <= 0x33FF)
                    || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xA000 && c <= 0xA4CF)
                    || (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F)
                    || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6))
                    return 2;   // 超 BMP 立体字以 surrogate 对出现：两字符各计 1 列，合计 2 列，正确
                return 1;
            }

            // ---- 左侧内容出口（TextWriter 重载） ----
            // 每次 Write 调用结束必须 FlushChunk：无换行提示（“请输入账号: ”）、密码回显 *、
            // \r 下载进度条都依赖立即上屏；这也还原了原版 TeeWriter 一次调用一次日志追加的粒度
            public override void Write(char value) { lock (_sync) { SyncCol(); Emit(value); FlushChunk(); } }
            public override void Write(string value)
            {
                if (value == null) return;
                lock (_sync) { SyncCol(); for (int i = 0; i < value.Length; i++) Emit(value[i]); FlushChunk(); }
            }
            public override void WriteLine(string value)
            {
                lock (_sync)
                {
                    SyncCol();
                    if (value != null) for (int i = 0; i < value.Length; i++) Emit(value[i]);
                    EmitNewline();
                }
            }
            public override void WriteLine() { lock (_sync) { SyncCol(); EmitNewline(); } }

            private void SyncCol()
            {
                try
                {
                    int lw = LeftWidth();
                    int c = Console.CursorLeft;
                    if (c >= lw)
                    {
                        // 真实光标漂进面板区（如超长输入回显）：先真实换行归零再写
                        WriteLineBreak();
                    }
                    else _col = c;
                }
                catch { }
            }

            private void Emit(char c)
            {
                if (c == '\n') { WriteLineBreak(); return; }
                if (c == '\r') { FlushChunk(); _log.Write("\r"); _col = 0; return; }
                if (c == '\b') { FlushChunk(); _log.Write("\b"); if (_col > 0) _col--; return; }
                if (c < 32) c = '?';
                int w = CharWidth(c);
                if (_col + w > LeftWidth()) WriteLineBreak();
                _chunk.Append(c);
                _col += w;
            }

            private void EmitNewline() { WriteLineBreak(); }

            private void FlushChunk()
            {
                if (_chunk.Length == 0) return;
                _log.Write(_chunk.ToString());
                _chunk.Length = 0;
            }

            // 换行 + 滚动感知重绘：buffer==window 时滚动是缓冲整体上移、srWindow.T 恒 0，
            // 只能靠「光标在视口底行 + 换行」判定发生了滚动（此时必须强制重绘面板）
            private void WriteLineBreak()
            {
                bool bottom = CursorAtBottom();
                FlushChunk();
                _log.Write("\n");
                _col = 0;
                Draw(bottom);
            }

            private bool CursorAtBottom()
            {
                try
                {
                    IntPtr h = GetStdHandle(StdOutputHandle);
                    CONSOLE_SCREEN_BUFFER_INFO info;
                    if (!GetConsoleScreenBufferInfo(h, out info)) return false;
                    int hgt = info.srWindow.B - info.srWindow.T + 1;
                    return info.dwCursorPosition.Y >= info.srWindow.T + hgt - 1;
                }
                catch { return false; }
            }

            // ---- 面板绘制 ----
            // 结构层（边框/空白/QR）用 WriteConsoleOutputW 直写缓冲单元格：无 ANSI 字节量、不经过
            // VT 解析器（滚动后每行重画也不会丢字节/阻塞）、不动光标。品牌/网址/文案 4 行含 CJK
            // 仍走 VT 短码（规避 CHAR_INFO 宽字符 lead/trail 配对坑），单次 ~400B。颜色为 16 色：
            // conhost 下 DarkCyan = 品牌青 RGB(0,128,128)；WT 主题下青系略偏蓝。

            public void Draw() { Draw(false); }

            public void Draw(bool force)
            {
                lock (_sync)
                {
                    try
                    {
                        IntPtr h = GetStdHandle(StdOutputHandle);
                        SyncViewport(h);   // buffer==视口 且顶格，否则两套坐标系分裂（客户机实测）
                        CONSOLE_SCREEN_BUFFER_INFO info;
                        if (!GetConsoleScreenBufferInfo(h, out info)) return;
                        int W = info.srWindow.R - info.srWindow.L + 1;
                        int H = info.srWindow.B - info.srWindow.T + 1;
                        int viewTop = info.srWindow.T;
                        if (!force && viewTop == _lastViewTop && W == _lastW && H == _lastH) return;
                        _lastViewTop = viewTop; _lastW = W; _lastH = H;
                        DiagSnapshot(h, force ? "draw-init" : "draw-scroll");   // 记录每次重绘时的真实视口状态
                        int bw = Math.Min(PanelW, W - 1);
                        int bx = W - bw;
                        int innerW = bw - 2;
                        int lw = bx - 2;
                        int saveC = info.dwCursorPosition.X, saveR = info.dwCursorPosition.Y;
                        if (saveC >= lw) saveC = Math.Max(0, lw - 1);
                        if (saveC < 0) saveC = 0;
                        if (saveR < viewTop) saveR = viewTop; else if (saveR >= viewTop + H) saveR = viewTop + H - 1;

                        // 结构层：黑底清残留 + 青色细边框（全 VT 视口相对行——终端自己维护视口语义，
                        // 免疫 buffer/视口分裂与桥接实现差异；WriteConsoleOutput 的 buffer 绝对坐标
                        // 在 scrollback/视口漂移的终端上与本应用脱节，客户机已实测弃用）
                        StringBuilder sb = new StringBuilder(256);
                        for (int y = 0; y < H; y++)
                        {
                            sb.Length = 0;
                            sb.Append(At(y, bx));
                            if (y == 0) sb.Append("\x1b[36m┌").Append('─', bw - 2).Append("┐");
                            else if (y == H - 1) sb.Append("\x1b[36m└").Append('─', bw - 2).Append("┘");
                            else sb.Append("\x1b[36m│\x1b[40m").Append(' ', bw - 2).Append("\x1b[36m│");
                            sb.Append(Reset);
                            _raw.Write(sb.ToString());
                        }
                        // 内容（垂直居中）：QR（37 模块，半块 1 字符列 = 1 模块列；黑白 16 色短码 RLE）
                        int innerH = H - 2;
                        int top = 1 + (innerH - ContentH) / 2;
                        if (top < 1) top = 1;
                        int qrX = 1 + (innerW - QrModules) / 2;
                        int qrY = top + 6;
                        for (int pr = 0; pr < QrCellRows && qrY + pr < H - 1; pr++)
                        {
                            int mTop = pr * 2, mBot = mTop + 1;
                            sb.Length = 0;
                            sb.Append(At(qrY + pr, bx + qrX));
                            bool pTop = false, pBot = false, first = true;
                            for (int c2 = 0; c2 < QrModules; c2++)
                            {
                                bool dTop = KefuQrMatrix[mTop][c2] == '1';
                                bool dBot = mBot < QrModules && KefuQrMatrix[mBot][c2] == '1';
                                if (first || dTop != pTop || dBot != pBot)
                                {
                                    sb.Append(dTop ? "\x1b[30m" : "\x1b[37m").Append(dBot ? "\x1b[40m" : "\x1b[47m");
                                    pTop = dTop; pBot = dBot; first = false;
                                }
                                sb.Append(dTop == dBot ? ' ' : '▀');
                            }
                            sb.Append(Reset);
                            _raw.Write(sb.ToString());
                        }

                        // 文字层（含 CJK，覆盖在结构层之上）：VT CUP 定位是【视口相对】行
                        _raw.Write(At(top, bx + 1 + (innerW - 12) / 2)
                          + "\x1b[97mXode\x1b[36m AI \x1b[90m平台\x1b[0m");
                        _raw.Write(At(top + 2, bx + 1 + (innerW - 22) / 2) + "\x1b[90m复制网址到浏览器注册\x1b[0m");
                        _raw.Write(At(top + 3, bx + 1 + (innerW - 23) / 2) + "\x1b[96mhttps://hub.xodeai.com\x1b[0m");
                        _raw.Write(At(top + 5, bx + 1 + (innerW - 28) / 2) + "\x1b[90m微信扫码联系客服，领\x1b[96m免费额度\x1b[0m");
                        _raw.Write(At(saveR - viewTop, saveC));   // 光标还原到左区（视口相对）
                        if (force) DiagSnapshot(h, "draw");
                    }
                    catch { }
                }
            }

            // ---- 本机 Win32 ----
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr GetStdHandle(int nStdHandle);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, COORD dwSize);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetConsoleWindowInfo(IntPtr hConsoleOutput, bool bAbsolute, ref SMALL_RECT lpConsoleWindow);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool SetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFO_EX lpConsoleCurrentFontEx);
            [DllImport("kernel32.dll")]
            private static extern IntPtr GetConsoleWindow();
            [DllImport("user32.dll")]
            private static extern int GetSystemMetrics(int nIndex);
            [StructLayout(LayoutKind.Sequential)]
            private struct RECT { public int Left, Top, Right, Bottom; }
            [DllImport("user32.dll")]
            private static extern bool GetWindowRect(IntPtr hWnd, ref RECT lpRect);
            [DllImport("user32.dll")]
            private static extern bool GetClientRect(IntPtr hWnd, ref RECT lpRect);

            private const int StdOutputHandle = -11;
            private const int SM_CXFULLSCREEN = 16;   // 屏幕宽-任务栏
            private const int SM_CYFULLSCREEN = 17;   // 屏幕高-任务栏

            [StructLayout(LayoutKind.Sequential)]
            private struct COORD
            {
                public short X; public short Y;
                public COORD(short x, short y) { X = x; Y = y; }
            }
            [StructLayout(LayoutKind.Sequential)]
            private struct SMALL_RECT { public short L; public short T; public short R; public short B; }
            [StructLayout(LayoutKind.Sequential)]
            private struct CONSOLE_SCREEN_BUFFER_INFO
            {
                public COORD dwSize;
                public COORD dwCursorPosition;
                public ushort wAttributes;
                public SMALL_RECT srWindow;
                public COORD dwMaximumWindowSize;
            }
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct CONSOLE_FONT_INFO_EX
            {
                public int cbSize;
                public uint nFont;
                public COORD dwFontSize;
                public int FontFamily;
                public int FontWeight;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try { Console.Title = "Xode AI — Codex App 一键安装程序"; } catch { }
            // 块字符（▀）依赖 UTF-8 输出，必须最先设置（GBK 控制台会把块字符变 '?'）
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            _consoleReal = Console.Out;   // 面板绘制用的裸控制台（在任何包装之前捕获）
            foreach (string a in args)
            {
                string arg = a; string val = "";
                int eq = a.IndexOf('=');
                if (eq > 0) { arg = a.Substring(0, eq); val = a.Substring(eq + 1); }
                if (arg == "-h" || arg == "-?" || arg == "--help") { ShowHelp = true; }
                if (arg == "-SkipChecksum") SkipChecksum = true;
                else if (arg == "-SkipShortcut") SkipShortcut = true;
                else if (arg == "-SkipApi") SkipApi = true;
                else if (arg == "-SkipZhCn") SkipZhCn = true;
                else if (arg == "-TestSub2api" || arg == "-TestRelay") TestSub2api = true;
                else if (arg == "-PanelProbe") PanelProbe = true;
                else if (arg == "-NoElevate") NoElevate = true;
                else if (arg == "-ApiBaseUrl") ApiBaseUrl = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-ApiKey") ApiKey = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-ApiModel") ApiModel = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-Site") Site = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-Email") Email = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-Password") Password = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-LogFile") LogFile = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-Logo") XodeLogoParam = (eq > 0 ? val : NextArg(args, a)).Trim();
            }

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            // 日志落盘（-LogFile）：提权后的子进程输出无法被外部管道捕获，用它留档排障
            if (LogFile.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(LogFile)));
                    Console.SetOut(new TeeWriter(Console.Out, LogFile));
                }
                catch { }
            }

            // 右侧品牌信息面板（Xode AI 平台 / 复制网址注册 / 客服二维码）；初始化失败则退化为普通全宽输出
            _panel = PanelUi.TryCreate(_consoleReal);
            if (ShowHelp) { PrintUsage(); Pause(); return 0; }

            // 测试模式：仅验证 sub2api 登录/建 Key/写配置（无需管理员、不安装不汉化）
            if (TestSub2api) { int rc = RunSub2apiTest(); Pause(); return rc; }

            // 远程排障：几何探测标尺（区分 .NET API 定位与 VT 定位在终端上的实际渲染位置）
            if (PanelProbe)
            {
                PanelUi.Probe(_panel, _consoleReal);
                Pause();
                return 0;
            }

            // 启动即要求输入并验证中转站账号密码（提权之前完成：验证不通过不进入安装）
            Banner();
            try { GetArch(); }
            catch (Exception ex) { Fail(ex.Message); Pause(); return 1; }
            string startToken = null;
            bool preManualKey = false, preSkipApi = false;
            if (!SkipApi && string.IsNullOrEmpty(ApiKey))
            {
                startToken = Sub2apiLoginFlow();
                if (startToken == null)
                {
                    Console.Write("  是否改用手动粘贴 API Key 完成配置？[Y/n] ");
                    string ans = SafeReadLine().Trim().ToLower();
                    if (ans == "" || ans == "y" || ans == "yes")
                    {
                        Console.Write("  API Key（格式 sk-xxxxxx）: ");
                        string mk = SafeReadLine().Trim();
                        if (mk.Length > 0) { ApiKey = mk; preManualKey = true; Ok("将使用手动提供的 API Key。"); }
                        else { preSkipApi = true; Warn("未提供 Key，将跳过聚合 API 配置。"); }
                    }
                    else { preSkipApi = true; Warn("已选择跳过 Xode AI平台 配置，仅安装应用。"); }
                }
            }

            if (!NoElevate && !IsAdministrator())
            {
                Console.WriteLine("请求管理员权限，请在 UAC 弹窗中点击“是”...");
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    psi.FileName = Process.GetCurrentProcess().MainModule.FileName;
                    StringBuilder sb = new StringBuilder();
                    foreach (string a in args) sb.Append(" \"").Append(a.Replace("\"", "\\\"")).Append("\"");
                    // 透传启动时验证的凭据/决定，避免提权窗口二次输入
                    if (!SkipApi && Email.Length > 0 && Password.Length > 0)
                        sb.Append(" \"-Email\" \"").Append(Email.Replace("\"", "\\\""))
                          .Append("\" \"-Password\" \"").Append(Password.Replace("\"", "\\\"")).Append("\"");
                    if (preManualKey) sb.Append(" \"-ApiKey\" \"").Append(ApiKey.Replace("\"", "\\\"")).Append("\"");
                    if (preSkipApi) sb.Append(" \"-SkipApi\"");
                    psi.Arguments = sb.ToString();
                    Process.Start(psi);
                    return 0;
                }
                catch (Exception ex) { Console.WriteLine("提权失败: " + ex.Message); Pause(); return 3; }
            }

            try
            {
                // 提权进入安装步骤前先关闭运行中的 Codex 桌面端：MSIX 覆盖安装、汉化补丁覆盖、
                // 图标品牌化都会被运行中的应用占用文件；先优雅关闭（给保存状态留 3 秒）再强杀
                CloseCodexApp();

                string arch = GetArch();
                Ok("系统架构: " + arch);

                // 步骤 1/11：登录 sub2api（启动时已验证 → 直接复用其 token；否则用透传凭据静默登录）
                string token = startToken;
                bool manualKey = (ApiKey != null && ApiKey.Trim().Length > 0);
                if (token == null && !SkipApi && !manualKey)
                {
                    token = Sub2apiLoginFlow();
                    if (token == null)
                    {
                        Console.Write("  是否改用手动粘贴 API Key 完成配置？[Y/n] ");
                        string ans = SafeReadLine().Trim().ToLower();
                        if (ans == "" || ans == "y" || ans == "yes") manualKey = true;
                    }
                }
                else if (manualKey)
                {
                    Ok("使用命令行提供的 API Key（跳过 Xode AI平台 登录）");
                }

                // 步骤 2/11：检查更新 → 下载并安装 Codex Desktop（已装且最新则跳过 1GB 下载）
                AppxInfo installedNow = GetCodexAppx();
                if (installedNow == null)
                {
                    // 手动卸载后机器级暂存包（provisioned）通常仍在：提权时直接从暂存补注册当前
                    // 用户，避免 1GB 重复下载；补注册失败则走正常下载安装。
                    AppxInfo staged = GetStagedCodex();
                    if (staged != null && RegisterStagedUser(staged))
                    {
                        installedNow = GetCodexAppx();
                        if (installedNow != null)
                            Ok("检测到机器级暂存包，已补注册当前用户（版本 " + installedNow.Version + "），无需重新下载");
                    }
                }
                string installedVer = installedNow != null ? installedNow.Version : null;
                string msix = GetMsix(arch, installedVer);
                AppxInfo appx;
                if (msix == null)
                {
                    Ok("Codex 已是最新版本（" + installedVer + "），跳过下载安装");
                    appx = GetCodexAppx();
                }
                else
                {
                    string latestVer = ExtractVersionFromPackageName(Path.GetFileName(msix));
                    appx = InstallMsix(msix, latestVer);
                }

                // 步骤 3-5 + 7/8/10：创建/复用 Key → 按 sub2api 模板写配置
                string doneKey = "";
                if (!SkipApi)
                {
                    if (manualKey)
                    {
                        ConfigureApiManual();
                    }
                    else if (token != null)
                    {
                        Step("3/11 创建/复用 API Key「" + Sub2apiKeyName + "」");
                        string primaryGrp;
                        List<GroupKeyInfo> s2Keys = Sub2apiResolveKeys(token, out primaryGrp);
                        doneKey = s2Keys[0].Key;
                        foreach (GroupKeyInfo ki in s2Keys)
                            Ok("分组 " + ki.GroupName + "#" + ki.GroupId + ": " + MaskKey(ki.Key)
                               + (ki.ProviderId == "OpenAI" ? "（默认）" : "")
                               + (ki.Model.Length > 0 ? " → " + ki.Model + (ki.Effort.Length > 0 ? "/" + ki.Effort : "") : ""));
                        Step("4/11 写入 Codex 配置（搜索=live / Full Access / 记忆=开 / 更新检查=关）");
                        WriteSub2apiConfigToml(s2Keys);
                        WriteAuthJson(doneKey);
                    }
                }

                // 桌面设置页「完全访问权限」开关的底层状态（config.toml 的 approval_policy/sandbox_mode 对其无效，实测）
                Step("5/11 预置完全访问权限（对话内默认免批准）");
                EnsureFullAccessDefault();

                // 步骤 6-7/11：汉化（新架构=启动器，旧架构=汉化包）
                string patchedExe = null;
                if (!SkipZhCn) patchedExe = LocalizeCodex(appx);
                else Warn("已跳过汉化（-SkipZhCn）");

                // Xode AI 品牌化（桌面/启动器图标）
                ApplyXodeBranding();

                // 步骤 8/11：插件市场（中国内地直连，本地目录源）
                EnsurePluginMarketplace();

                // 桌面快捷方式（汉化成功 → 指向汉化目标；否则指向 Store 原版）
                if (!SkipShortcut)
                {
                    if (patchedExe != null) CreateDesktopShortcutToExe(patchedExe);
                    else CreateDesktopShortcut(appx);
                }

                Step("9/11 验证安装结果");
                AppxInfo final = GetCodexAppx();
                if (final != null)
                {
                    Ok("Codex Desktop: " + final.Name + " " + final.Version);
                    Ok("PackageFamilyName: " + final.PackageFamilyName);
                }
                PrintCompletion(appx, doneKey, patchedExe);
            }
            catch (Exception ex)
            {
                Fail("执行失败: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Pause();
                return 1;
            }
            Pause();
            return 0;
        }

        // ================= 工具 =================
        private static void Step(string msg) { Console.WriteLine("\n==> " + msg); }
        private static string NextArg(string[] args, string current)
        {
            for (int i = 0; i < args.Length; i++)
                if (args[i] == current && i + 1 < args.Length) return args[i + 1];
            return "";
        }
        private static void Ok(string msg) { Console.WriteLine("  [OK] " + msg); }
        private static void Warn(string msg) { Console.WriteLine("  [!!] " + msg); }
        private static void Fail(string msg) { Console.WriteLine("  [XX] " + msg); }
        private static void Banner()
        {
            Console.WriteLine("===============================================");
            Console.WriteLine(" Codex App（ChatGPT Desktop）一键安装程序（" + BuildInfo.Time + "版本）");
            Console.WriteLine("===============================================");
            Console.WriteLine(" 说明: 自动安装 Codex App、配置 Xode AI平台、启用汉化与中文插件市场");
        }
        private static void Pause()
        {
            try
            {
                if (Console.IsInputRedirected) return;
                Console.WriteLine("\n按任意键退出...");
                try { if (_panel != null) _panel.Draw(true); } catch { }   // 最终画面补画
                Console.ReadKey(true);
            }
            catch { }
        }
        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
        // 关闭运行中的 Codex 桌面端 GUI。只杀 GUI：新架构进程名 ChatGPT.exe（26.1106+）；
        // 旧架构 Codex.exe 仅限包内路径（%LOCALAPPDATA%\OpenAI\Codex\bin 下的 codex.exe 是
        // CLI，用户可能在跑 CLI 会话，不能误杀）
        private static void CloseCodexApp()
        {
            int killed = 0;
            foreach (Process p in Process.GetProcessesByName("ChatGPT"))
            {
                try { if (GracefulKill(p)) killed++; } catch { }
            }
            foreach (Process p in Process.GetProcessesByName("Codex"))
            {
                try
                {
                    string path = "";
                    try { path = p.MainModule.FileName ?? ""; } catch { }
                    if (path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) < 0
                        && path.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (GracefulKill(p)) killed++;
                }
                catch { }
            }
            if (killed > 0) Ok("已关闭正在运行的 Codex 桌面端（" + killed + " 个进程）");
        }
        private static bool GracefulKill(Process p)
        {
            try { if (p.CloseMainWindow()) p.WaitForExit(3000); } catch { }
            try
            {
                if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); }
                return p.HasExited;
            }
            catch { return false; }
        }
        private static string GetArch()
        {
            string envArch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            if (envArch != null && envArch.IndexOf("ARM64", StringComparison.OrdinalIgnoreCase) >= 0) return "arm64";
            if (envArch != null && envArch.IndexOf("AMD64") >= 0) return "x64";
            throw new Exception("不支持的架构: " + envArch + "（Codex 桌面应用仅支持 x64/arm64）");
        }
        private static string SafeReadLine()
        {
            try { if (_panel != null) _panel.Draw(true); } catch { }   // 交互静止期补画：自愈初始竞态错位
            try { return Console.ReadLine() ?? ""; }
            catch { return ""; }
        }
        // 密码输入（回显 *；输入被重定向时退化为普通读取）
        private static string ReadPassword()
        {
            try { if (_panel != null) _panel.Draw(true); } catch { }   // 交互静止期补画
            try
            {
                if (Console.IsInputRedirected) return SafeReadLine();
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    ConsoleKeyInfo k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Enter) break;
                    if (k.Key == ConsoleKey.Backspace)
                    {
                        if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                    }
                    else if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); Console.Write("*"); }
                }
                Console.WriteLine();
                return sb.ToString();
            }
            catch { return SafeReadLine(); }
        }
        private static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.Length <= 12) return key;
            return key.Substring(0, 8) + "…" + key.Substring(key.Length - 4);
        }
        private static string Trunc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 200 ? s : s.Substring(0, 200) + "…";
        }

        // ================= HTTP / JSON 工具 =================
        // JSON 请求（POST/GET/DELETE）。4xx/5xx 时仍返回响应体（sub2api 错误结构为 JSON），网络异常才抛出。
        // 先走系统代理，网络异常时回退直连（部分系统代理会拦截国内外正常请求）
        private static string HttpJson(string method, string url, string body, string bearer)
        {
            Exception last = null;
            for (int mode = 0; mode < 2; mode++)
            {
                try { return HttpJsonOnce(method, url, body, bearer, mode == 1); }
                catch (Exception ex) { last = ex; }
            }
            throw new Exception((last != null ? last.Message : "请求失败") + "（已尝试系统代理与直连）");
        }

        private static string HttpJsonOnce(string method, string url, string body, string bearer, bool bypassProxy)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = UserAgent;
            req.Accept = "application/json";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = 60000; req.ReadWriteTimeout = 60000;
            if (bypassProxy) req.Proxy = null;
            if (!string.IsNullOrEmpty(bearer)) req.Headers.Add("Authorization", "Bearer " + bearer);
            if (body != null)
            {
                byte[] b = Encoding.UTF8.GetBytes(body);
                req.ContentLength = b.Length;
                using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                try
                {
                    HttpWebResponse er = ex.Response as HttpWebResponse;
                    if (er != null)
                    using (StreamReader sr = new StreamReader(er.GetResponseStream(), Encoding.UTF8))
                        return sr.ReadToEnd(); // 服务器有响应（4xx/5xx）：错误结构交给上层解析，无需换线路
                }
                catch { }
                throw new Exception(method + " " + url + " 失败" + (bypassProxy ? "(直连)" : "(系统代理)") + ": " + ex.Message);
            }
        }

        private static Dictionary<string, object> ParseJson(string json)
        {
            try
            {
                JavaScriptSerializer jss = new JavaScriptSerializer();
                jss.MaxJsonLength = int.MaxValue;
                return jss.Deserialize<Dictionary<string, object>>(json);
            }
            catch { return null; }
        }
        private static object JD(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v)) ? v : null;
        }
        private static string JS(Dictionary<string, object> d, string key)
        {
            object v = JD(d, key);
            return v == null ? "" : v.ToString();
        }
        // JSON 数组 → 字典列表。实测 JavaScriptSerializer 把数组还原为 System.Collections.ArrayList，
        // 用非泛型 IEnumerable 兜底以兼容 List<object> / object[] / ArrayList 等所有形态
        private static List<Dictionary<string, object>> AsDictList(object o)
        {
            List<Dictionary<string, object>> r = new List<Dictionary<string, object>>();
            if (o is Dictionary<string, object>) { r.Add((Dictionary<string, object>)o); return r; }
            IEnumerable seq = o as IEnumerable;
            if (o is string || seq == null) return r;
            foreach (object x in seq)
            {
                Dictionary<string, object> d = x as Dictionary<string, object>;
                if (d != null) r.Add(d);
            }
            return r;
        }
        // sub2api 管理端响应包装 {code,message,data}；网关响应无 code
        private static bool ApiCodeOk(Dictionary<string, object> root, out string msg)
        {
            msg = "";
            object code = JD(root, "code");
            if (code == null) return true;
            long c;
            try { c = Convert.ToInt64(code); } catch { return true; }
            if (c == 0) return true;
            msg = JS(root, "message");
            if (msg.Length == 0) msg = "错误 code=" + c;
            return false;
        }

        private static string GetWebString(string url)
        {
            try { return GetWebStringOnce(url, false); }
            catch { return GetWebStringOnce(url, true); }
        }

        private static string GetWebStringOnce(string url, bool bypassProxy)
        {
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", UserAgent);
                if (bypassProxy) wc.Proxy = null;
                return wc.DownloadString(url);
            }
        }

        // ================= sub2api 客户端 =================
        private static string GetSite()
        {
            string s = (Site != null && Site.Trim().Length > 0) ? Site.Trim() : Sub2apiSiteDefault;
            return s.TrimEnd('/');
        }

        // 登录交互（最多 3 次），成功返回 access_token；全部失败返回 null
        private static string Sub2apiLoginFlow()
        {
            Step("1/11 连接 Xode AI平台: " + GetSite());
            Console.WriteLine("  说明: 将登录 Xode AI平台 并自动创建/复用 API Key「" + Sub2apiKeyName + "」");
            string email = Email != null ? Email.Trim() : "";
            string pwd = Password != null ? Password.Trim() : "";
            bool fromArgs = email.Length > 0 && pwd.Length > 0;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (email.Length == 0 || pwd.Length == 0)
                    {
                        Console.Write("  请输入 Xode AI平台 账号（邮箱）" + (attempt > 1 ? "（第 " + attempt + "/3 次）" : "") + ": ");
                        email = SafeReadLine().Trim();
                        Console.Write("  请输入密码: ");
                        pwd = ReadPassword();
                    }
                    if (email.Length == 0 || pwd.Length == 0) { Fail("账号或密码为空"); email = ""; pwd = ""; continue; }
                    string token = Sub2apiLogin(GetSite(), email, pwd);
                    Ok("登录成功: " + email);
                    // 记录凭据：提权后子进程透传使用，避免二次输入
                    Email = email;
                    Password = pwd;
                    return token;
                }
                catch (Exception ex)
                {
                    Fail("登录失败（第 " + attempt + "/3 次）: " + FriendlyLoginError(Trunc(ex.Message)));
                    // 参数提供的账密失败后不再交互重试（避免无头自动化卡在输入提示）
                    if (fromArgs) { Warn("命令行提供的账号密码登录失败，不再重试"); break; }
                    email = ""; pwd = "";
                }
            }
            return null;
        }

        private static string FriendlyLoginError(string raw)
        {
            if (raw.IndexOf("INVALID_CREDENTIALS", StringComparison.OrdinalIgnoreCase) >= 0) return "邮箱或密码错误";
            if (raw.IndexOf("invalid email or password", StringComparison.OrdinalIgnoreCase) >= 0) return "邮箱或密码错误";
            if (raw.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0 || raw.IndexOf("turnstile", StringComparison.OrdinalIgnoreCase) >= 0)
                return "站点开启了登录人机验证码，无法自动登录（请在站点后台关闭验证码后重试）";
            return raw;
        }

        private static string Sub2apiLogin(string site, string email, string pwd)
        {
            string resp = HttpJson("POST", site + "/api/v1/auth/login",
                "{\"email\":\"" + EscapeJson(email) + "\",\"password\":\"" + EscapeJson(pwd) + "\"}", null);
            Dictionary<string, object> root = ParseJson(resp);
            if (root == null) throw new Exception("登录响应解析失败: " + Trunc(resp));
            object tfa = JD(root, "requires_2fa");
            Dictionary<string, object> data0 = JD(root, "data") as Dictionary<string, object>;
            if (tfa == null && data0 != null) tfa = JD(data0, "requires_2fa");
            if (tfa is bool && (bool)tfa)
            {
                string temp = JS(root, "temp_token");
                if (temp.Length == 0 && data0 != null) temp = JS(data0, "temp_token");
                Console.Write("  账号已开启两步验证（2FA），请输入 6 位动态码: ");
                string code = SafeReadLine().Trim();
                resp = HttpJson("POST", site + "/api/v1/auth/login/2fa",
                    "{\"temp_token\":\"" + EscapeJson(temp) + "\",\"totp_code\":\"" + EscapeJson(code) + "\"}", null);
                root = ParseJson(resp);
                if (root == null) throw new Exception("2FA 响应解析失败: " + Trunc(resp));
            }
            string msg;
            if (!ApiCodeOk(root, out msg)) throw new Exception(msg);
            Dictionary<string, object> data = JD(root, "data") as Dictionary<string, object>;
            string token = data != null ? JS(data, "access_token") : "";
            if (token.Length == 0) token = JS(root, "access_token");
            if (token.Length == 0) throw new Exception("登录成功但未返回 access_token");
            return token;
        }

        // 每个可用分组对应的 Key 信息（ProviderId: 首个分组 = "OpenAI"，其余 = "OpenAI-G<gid>"）
        private class GroupKeyInfo
        {
            public string GroupId, GroupName, Key, Model, Effort, ProviderId;
        }

        // 解析 Key 绑定的分组（单 Key，按计费安全优先级）：
        //   1. 用户活跃订阅的分组——订阅了哪个套餐就绑哪个分组，计费严格走套餐，杜绝流量漏进按量
        //   2. 聚合分组（platform=composite）——单 Key 全部已路由模型（仅限同计费层聚合）
        //   3. 兜底：第一个可用分组（通常是免费组）
        // 并清理旧方案遗留的按分组命名键（codex桌面端-G<gid>）
        private static List<GroupKeyInfo> Sub2apiResolveKeys(string token, out string primaryGroupDesc)
        {
            primaryGroupDesc = "未分组";
            List<GroupKeyInfo> result = new List<GroupKeyInfo>();
            List<Dictionary<string, object>> items = ListKeys(token);
            List<Dictionary<string, object>> groups = AvailableOpenAiGroups(token);

            // 1) 订阅分组优先
            foreach (string sgid in SubscribedGroupIds(token))
            {
                Dictionary<string, object> g = FindGroupById(groups, sgid);
                if (g == null) { Warn("订阅对应的分组 " + sgid + " 不在可用列表中"); continue; }
                GroupKeyInfo gi = ResolveKeyForGroup(token, items, g);
                if (gi == null) continue;
                Ok("检测到您的套餐订阅 → Key 绑定该分组（流量全部走套餐计费）");
                result.Add(gi);
                break; // 单 Key：订阅分组即全部
            }

            // 2) 默认分组：GPT 按量计费（确定性绑定，不询问用户）
            if (result.Count == 0)
            {
                Dictionary<string, object> g = FindGroupByNameKeyword(groups, DefaultGroupName, DefaultGroupNameKeyword);
                if (g != null)
                {
                    GroupKeyInfo gi = ResolveKeyForGroup(token, items, g);
                    if (gi != null) result.Add(gi);
                }
                else Warn("未找到默认分组「" + DefaultGroupName + "」，回退到第一个可用分组");
            }

            // 3) 聚合分组（platform=composite）：单 Key 访问所有已路由模型（仅限同计费层聚合）
            if (result.Count == 0)
            {
                foreach (Dictionary<string, object> g in groups)
                {
                    if (!JS(g, "platform").Equals("composite", StringComparison.OrdinalIgnoreCase)) continue;
                    GroupKeyInfo gi = ResolveKeyForGroup(token, items, g);
                    if (gi == null) continue;
                    Warn("使用聚合分组——请确认其账户属于同一计费层，避免订阅流量漏进按量计费");
                    result.Add(gi);
                    break;
                }
            }

            // 4) 兜底：第一个可用分组
            if (result.Count == 0 && groups.Count > 0)
            {
                PrintGroups(groups);
                GroupKeyInfo gi = ResolveKeyForGroup(token, items, groups[0]);
                if (gi != null) result.Add(gi);
            }

            // 5) 无可用分组：退回未分组 Key（个别站点允许）
            if (result.Count == 0)
            {
                string errUngrouped = "";
                Dictionary<string, object> k = CreateKeySmart(token, Sub2apiKeyName, null, out errUngrouped);
                if (k == null) throw new Exception("无法创建 API Key: " + Trunc(errUngrouped));
                Warn("Key 未绑定分组（若站点要求分组，请到站点后台为其选择分组）");
                GroupKeyInfo gi = new GroupKeyInfo { GroupId = "0", GroupName = "未分组", Key = JS(k, "key"), ProviderId = "OpenAI" };
                string m, e, ks;
                if (FetchCatalog(JS(k, "key"), out m, out e, out ks)) { gi.Model = m; gi.Effort = e; }
                result.Add(gi);
            }

            result[0].ProviderId = "OpenAI";
            primaryGroupDesc = result[0].GroupName + "#" + result[0].GroupId;
            return result;
        }

        // 为指定分组取/建 Key（命名「codex桌面端」），并清理旧方案遗留的 -G<gid> 键与分组不符的同名键
        private static GroupKeyInfo ResolveKeyForGroup(string token, List<Dictionary<string, object>> items, Dictionary<string, object> g)
        {
            long gid;
            if (!long.TryParse(JS(g, "id"), out gid)) return null;
            string gName = JS(g, "name");
            Dictionary<string, object> k = FindKeyByName(items, Sub2apiKeyName);
            if (k != null && JS(k, "group_id") != gid.ToString()) k = null; // 同名但分组不符 → 重建
            if (k == null)
            {
                string err = "";
                k = CreateKeySmart(token, Sub2apiKeyName, gid, out err);
                if (k == null) { Warn("分组 " + gName + "#" + gid + " 创建 Key 失败: " + Trunc(err)); return null; }
            }
            // 清理旧方案遗留的按分组命名键（codex桌面端-G<gid>）与分组不符的同名旧键
            foreach (Dictionary<string, object> old in FindKeysByPrefix(items, Sub2apiKeyName + "-G"))
                DeleteKey(token, JS(old, "id"));
            foreach (Dictionary<string, object> old in FindKeysByName(items, Sub2apiKeyName))
                if (JS(old, "group_id") != gid.ToString()) DeleteKey(token, JS(old, "id"));
            string km = "", ke = "", ks;
            GroupKeyInfo gi = new GroupKeyInfo { GroupId = gid.ToString(), GroupName = gName, Key = JS(k, "key"), ProviderId = "OpenAI" };
            if (FetchCatalog(JS(k, "key"), out km, out ke, out ks)) { gi.Model = km; gi.Effort = ke; }
            else Warn("分组 " + gName + "#" + gid + " 模型目录为空（" + ks + "）——请检查该组账户/渠道配置");
            return gi;
        }

        // 查询用户活跃订阅（GET /api/v1/subscriptions）
        private static List<Dictionary<string, object>> GetUserSubscriptions(string token)
        {
            try
            {
                string resp = HttpJson("GET", GetSite() + "/api/v1/subscriptions", null, token);
                Dictionary<string, object> root = ParseJson(resp);
                string msg;
                if (root == null || !ApiCodeOk(root, out msg)) return new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> subs = AsDictList(JD(root, "data"));
                if (subs.Count == 0)
                {
                    Dictionary<string, object> data = JD(root, "data") as Dictionary<string, object>;
                    subs = AsDictList(JD(data, "subscriptions") ?? JD(data, "items") ?? JD(root, "items"));
                }
                return subs;
            }
            catch { return new List<Dictionary<string, object>>(); }
        }

        // 套餐列表（GET /api/v1/payment/plans，含 plan→group_id 映射）
        private static List<Dictionary<string, object>> GetPaymentPlans(string token)
        {
            try
            {
                string resp = HttpJson("GET", GetSite() + "/api/v1/payment/plans", null, token);
                Dictionary<string, object> root = ParseJson(resp);
                string msg;
                if (root == null || !ApiCodeOk(root, out msg)) return new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> plans = AsDictList(JD(root, "data"));
                if (plans.Count == 0)
                {
                    Dictionary<string, object> data = JD(root, "data") as Dictionary<string, object>;
                    plans = AsDictList(JD(data, "plans") ?? JD(data, "items") ?? JD(root, "items"));
                }
                return plans;
            }
            catch { return new List<Dictionary<string, object>>(); }
        }

        // 用户活跃订阅对应的分组 id（订阅 → plan_id → 套餐 group_id）
        private static List<string> SubscribedGroupIds(string token)
        {
            List<string> r = new List<string>();
            List<Dictionary<string, object>> subs = GetUserSubscriptions(token);
            if (subs.Count == 0) return r;
            Dictionary<string, string> planGroup = new Dictionary<string, string>();
            foreach (Dictionary<string, object> pl in GetPaymentPlans(token))
                planGroup[JS(pl, "id")] = JS(pl, "group_id");
            foreach (Dictionary<string, object> s in subs)
            {
                string st = JS(s, "status");
                if (st.Length > 0 && st != "active" && st != "trialing") continue;
                string g = JS(s, "group_id");
                if (g.Length == 0 || g == "0")
                {
                    string pid = JS(s, "plan_id");
                    if (pid.Length > 0 && planGroup.ContainsKey(pid)) g = planGroup[pid];
                }
                if (g.Length > 0 && g != "0" && !r.Contains(g)) r.Add(g);
            }
            return r;
        }

        private static Dictionary<string, object> FindGroupById(List<Dictionary<string, object>> groups, string gid)
        {
            foreach (Dictionary<string, object> g in groups)
                if (JS(g, "id") == gid) return g;
            return null;
        }

        // 按名称匹配分组：先精确匹配默认分组名，再按关键字模糊匹配
        private static Dictionary<string, object> FindGroupByNameKeyword(List<Dictionary<string, object>> groups, string exactName, string keyword)
        {
            foreach (Dictionary<string, object> g in groups)
                if (JS(g, "name").Equals(exactName, StringComparison.OrdinalIgnoreCase)) return g;
            foreach (Dictionary<string, object> g in groups)
                if (JS(g, "name").Contains(keyword)) return g;
            return null;
        }

        // 改 Key 名称（PUT /api/v1/keys/<id>）
        private static bool UpdateKeyName(string token, string id, string newName)
        {
            try
            {
                string resp = HttpJson("PUT", GetSite() + "/api/v1/keys/" + id,
                    "{\"name\":\"" + EscapeJson(newName) + "\"}", token);
                Dictionary<string, object> root = ParseJson(resp);
                string msg;
                return root != null && ApiCodeOk(root, out msg);
            }
            catch { return false; }
        }

        private static List<Dictionary<string, object>> ListKeys(string token)
        {
            string resp = HttpJson("GET", GetSite() + "/api/v1/keys", null, token);
            Dictionary<string, object> root = ParseJson(resp);
            if (root == null) throw new Exception("API Key 列表响应解析失败");
            string msg;
            if (!ApiCodeOk(root, out msg)) throw new Exception("获取 API Key 列表失败: " + msg);
            Dictionary<string, object> data = JD(root, "data") as Dictionary<string, object>;
            List<Dictionary<string, object>> items = AsDictList(JD(root, "items") ?? JD(data, "items"));
            return items;
        }

        private static Dictionary<string, object> FindKeyByName(List<Dictionary<string, object>> items, string name)
        {
            Dictionary<string, object> best = null; string bestCreated = "";
            foreach (Dictionary<string, object> d in items)
            {
                if (JS(d, "name") != name) continue;
                string st = JS(d, "status");
                if (st.Length > 0 && st != "active") continue;
                string c = JS(d, "created_at");
                if (best == null || string.Compare(c, bestCreated, StringComparison.Ordinal) > 0) { best = d; bestCreated = c; }
            }
            return best;
        }

        private static List<Dictionary<string, object>> FindKeysByName(List<Dictionary<string, object>> items, string name)
        {
            List<Dictionary<string, object>> r = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> d in items)
            {
                if (JS(d, "name") != name) continue;
                string st = JS(d, "status");
                if (st.Length > 0 && st != "active") continue;
                r.Add(d);
            }
            return r;
        }

        private static List<Dictionary<string, object>> FindKeysByPrefix(List<Dictionary<string, object>> items, string prefix)
        {
            List<Dictionary<string, object>> r = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> d in items)
                if (JS(d, "name").StartsWith(prefix, StringComparison.Ordinal)) r.Add(d);
            return r;
        }

        private static string DescribeGroup(Dictionary<string, object> keyDto)
        {
            Dictionary<string, object> g = JD(keyDto, "group") as Dictionary<string, object>;
            if (g != null && JS(g, "name").Length > 0) return "分组 " + JS(g, "name") + "#" + JS(g, "id");
            string gid = JS(keyDto, "group_id");
            if (gid.Length > 0 && gid != "0") return "group_id=" + gid;
            return "未分组";
        }

        private static List<Dictionary<string, object>> AvailableOpenAiGroups(string token)
        {
            List<Dictionary<string, object>> r = new List<Dictionary<string, object>>();
            try
            {
                string resp = HttpJson("GET", GetSite() + "/api/v1/groups/available", null, token);
                Dictionary<string, object> root = ParseJson(resp);
                string msg;
                if (root == null || !ApiCodeOk(root, out msg)) return r;
                foreach (Dictionary<string, object> g in AsDictList(JD(root, "data")))
                {
                    string plat = JS(g, "platform");
                    if (plat.Length == 0 || plat.Equals("openai", StringComparison.OrdinalIgnoreCase)
                        || plat.Equals("composite", StringComparison.OrdinalIgnoreCase)) r.Add(g);
                }
                // 聚合分组（platform=composite）排在最前：存在即优先使用（单 Key 全模型）
                r.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
                {
                    bool ca = JS(a, "platform").Equals("composite", StringComparison.OrdinalIgnoreCase);
                    bool cb = JS(b, "platform").Equals("composite", StringComparison.OrdinalIgnoreCase);
                    if (ca != cb) return ca ? -1 : 1;
                    long ai, bi;
                    long.TryParse(JS(a, "id"), out ai);
                    long.TryParse(JS(b, "id"), out bi);
                    return ai.CompareTo(bi);
                });
            }
            catch { }
            return r;
        }

        // 打印可用分组清单（openai 平台）
        private static void PrintGroups(List<Dictionary<string, object>> groups)
        {
            Console.WriteLine("  可用分组（openai 平台，共 " + groups.Count + " 个）:");
            for (int i = 0; i < groups.Count; i++)
            {
                Dictionary<string, object> g = groups[i];
                string desc = JS(g, "description");
                Console.WriteLine("   [" + (i + 1) + "] " + JS(g, "name") + " (id=" + JS(g, "id") + ")" + (desc.Length > 0 ? " - " + desc : ""));
            }
        }

        // 创建 Key；重名等错误时自动加日期后缀重试
        private static Dictionary<string, object> CreateKeySmart(string token, string baseName, long? groupId, out string err)
        {
            Dictionary<string, object> k = CreateKey(token, baseName, groupId, out err);
            if (k == null && (err.IndexOf("exist", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("已存在") >= 0
                || err.IndexOf("重复") >= 0 || err.IndexOf("unique", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                string alt = baseName + "-" + DateTime.Now.ToString("yyyyMMdd");
                k = CreateKey(token, alt, groupId, out err);
            }
            return k;
        }

        private static Dictionary<string, object> CreateKey(string token, string name, long? groupId, out string err)
        {
            err = "";
            string body = "{\"name\":\"" + EscapeJson(name) + "\"";
            if (groupId.HasValue) body += ",\"group_id\":" + groupId.Value.ToString();
            body += "}";
            string resp = HttpJson("POST", GetSite() + "/api/v1/keys", body, token);
            Dictionary<string, object> root = ParseJson(resp);
            if (root == null) { err = "创建响应解析失败"; return null; }
            string msg;
            if (!ApiCodeOk(root, out msg)) { err = msg; return null; }
            Dictionary<string, object> data = JD(root, "data") as Dictionary<string, object>;
            if (data == null || JS(data, "key").Length == 0) { err = "创建成功但响应缺少 key 字段"; return null; }
            return data;
        }

        private static void DeleteKey(string token, string id)
        {
            try { HttpJson("DELETE", GetSite() + "/api/v1/keys/" + id, null, token); } catch { }
        }

        // 拉取 sub2api Codex 模型目录（GET /v1/models?client_version=...，需要 Key 的 Bearer 认证）
        // status: ok=成功 / empty=目录为空或网关拒绝（Key 未绑定分组等）/ error=网络或解析异常
        private static bool FetchCatalog(string apiKey, out string model, out string effort, out string status)
        {
            model = ""; effort = ""; status = "error";
            try
            {
                string resp = HttpJson("GET", GetSite() + "/v1/models?client_version=" + CodexClientVersion, null, apiKey);
                status = "empty";
                Dictionary<string, object> root = ParseJson(resp);
                if (root == null) return false;
                if (root.ContainsKey("error")) return false; // 网关错误（如 Key 未绑定分组不可用）
                List<Dictionary<string, object>> models = AsDictList(JD(root, "models"));
                if (models.Count == 0) return false;
                Dictionary<string, object> pick = null;
                foreach (Dictionary<string, object> d in models)
                    if (JS(d, "slug") == DefaultModel) { pick = d; break; }
                if (pick == null)
                    foreach (Dictionary<string, object> d in models)
                        if (JS(d, "slug").StartsWith("gpt", StringComparison.OrdinalIgnoreCase)) { pick = d; break; }
                if (pick == null) pick = models[0];
                model = JS(pick, "slug");
                effort = PickEffort(pick);
                status = "ok";
                return model.Length > 0;
            }
            catch { return false; }
        }

        private static string PickEffort(Dictionary<string, object> m)
        {
            string def = JS(m, "default_reasoning_level");
            if (def.Length > 0 && def != "none") return def;
            foreach (Dictionary<string, object> d in AsDictList(JD(m, "supported_reasoning_levels")))
            {
                string e = JS(d, "effort");
                if (e.Length > 0 && e != "none") return e;
            }
            return "";
        }

        // 测试模式：仅跑 sub2api 登录/建 Key/写配置
        private static int RunSub2apiTest()
        {
            Banner();
            Ok("测试模式: 仅验证 Xode AI平台 登录 / 建 Key / 配置写入（不安装、不汉化、无需管理员）");
            try
            {
                string token = Sub2apiLoginFlow();
                if (token == null) { Fail("登录未通过，测试失败"); return 1; }
                Step("3/11 创建/复用 API Key「" + Sub2apiKeyName + "」");
                string primaryGrp;
                List<GroupKeyInfo> keys = Sub2apiResolveKeys(token, out primaryGrp);
                foreach (GroupKeyInfo ki in keys)
                    Ok("分组 " + ki.GroupName + "#" + ki.GroupId + ": " + MaskKey(ki.Key)
                       + (ki.ProviderId == "OpenAI" ? "（默认）" : "")
                       + (ki.Model.Length > 0 ? " → " + ki.Model + (ki.Effort.Length > 0 ? "/" + ki.Effort : "") : ""));
                Step("5/11 写入 Codex 配置");
                WriteSub2apiConfigToml(keys);
                WriteAuthJson(keys[0].Key);
                EnsureFullAccessDefault();
                EnsurePluginMarketplace();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n 测试通过。");
                Console.ResetColor();
                return 0;
            }
            catch (Exception ex)
            {
                Fail("测试失败: " + Trunc(ex.Message));
                return 1;
            }
        }

        // ================= 下载（流式 + 实时进度） =================
        private static bool DownloadFile(string url, string dest, string desc, long minBytes)
        {
            Console.WriteLine("  下载中: " + desc + " (" + url + ")");
            Stopwatch sw = Stopwatch.StartNew();
            bool ok = false;
            // 原子写入：先写 .part，成功后整体改名——失败/中断不污染目标路径
            string part = dest + ".part";
            for (int attempt = 1; attempt <= 3 && !ok; attempt++)
            {
                HttpWebRequest req = null; HttpWebResponse resp = null; Stream stream = null; FileStream fs = null;
                if (File.Exists(part)) File.Delete(part);
                try
                {
                    req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.UserAgent = UserAgent;
                    req.Timeout = 30000; req.ReadWriteTimeout = 60000; req.AllowAutoRedirect = true;
                    if (attempt % 2 == 0) req.Proxy = null; // 重试时交替直连（系统代理可能拦截下载源）
                    resp = (HttpWebResponse)req.GetResponse();
                    long total = resp.ContentLength;
                    stream = resp.GetResponseStream();
                    fs = File.Create(part);
                    byte[] buf = new byte[65536];
                    long done = 0, lastBytes = 0;
                    DateTime lastTick = DateTime.Now;
                    Stopwatch progSw = Stopwatch.StartNew();
                    int n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        done += n;
                        if (progSw.ElapsedMilliseconds >= 200 && total > 0)
                        {
                            double dt = (DateTime.Now - lastTick).TotalSeconds;
                            double speed = Math.Max(done - lastBytes, 0) / Math.Max(dt, 0.001) / 1048576.0;
                            lastBytes = done; lastTick = DateTime.Now;
                            Console.Write(string.Format("\r  进度 {0,3:N0}% | {1,7:N1} / {2,7:N1} MB | {3,5:N2} MB/s ", Math.Min(100, done * 100.0 / total), done / 1048576.0, total / 1048576.0, speed));
                            progSw.Restart();
                        }
                    }
                    Console.WriteLine();
                    fs.Close(); stream.Close(); resp.Close();
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(part, dest);
                    ok = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Fail(string.Format("第 {0} 次下载失败: {1}", attempt, ex.Message));
                    try { if (fs != null) fs.Close(); } catch { }
                    try { if (stream != null) stream.Close(); } catch { }
                    try { if (resp != null) resp.Close(); } catch { }
                    try { File.Delete(part); } catch { }
                    if (attempt < 3) Thread.Sleep(2000);
                }
            }
            sw.Stop();
            if (!ok) { if (File.Exists(part)) File.Delete(part); return false; }
            if (!File.Exists(dest)) { Fail("下载失败: 文件不存在"); return false; }
            long size = new FileInfo(dest).Length;
            if (size < minBytes) { Fail("下载异常: 文件过小 (" + size + " bytes)"); File.Delete(dest); return false; }
            double avg = (size / 1048576.0) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            Ok(string.Format("已下载 {0} ({1:N1} MB, 平均 {2:N2} MB/s)", dest, size / 1048576.0, avg));
            return true;
        }

        // ================= 镜像仓库 =================
        // GitHub API 访问：api.github.com 直连优先，失败走 gh-proxy（国内线路）；
        // GetWebString 内部还带"系统代理→直连"回退
        private static string GetGitHubApiString(string apiPath)
        {
            List<string> urls = new List<string>();
            urls.Add("https://api.github.com/" + apiPath);
            foreach (string p in GhProxyPrefixes)
                urls.Add(p.Replace("https://github.com", "https://api.github.com") + "/" + apiPath);
            foreach (string url in urls)
            {
                try { string s = GetWebString(url); if (!string.IsNullOrEmpty(s)) return s; }
                catch { }
            }
            return null;
        }

        private static string GetGitHubLatestTag(string repo)
        {
            try
            {
                string json = GetGitHubApiString("repos/" + repo + "/releases/latest");
                Match m = Regex.Match(json ?? "", "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        // 从 release 资产中取当前架构的 MSIX 文件名（如 OpenAI.Codex_26.803.5235.0_x64__2p2nqsd0c76g0.Msix）
        private static string GetMsixAssetName(string tag, string arch)
        {
            try
            {
                string json = GetGitHubApiString("repos/" + MirrorRepo + "/releases/latest");
                Regex re = new Regex("\"name\"\\s*:\\s*\"(OpenAI\\.Codex_[^\"]*_" + arch + "__[^\"]*\\.Msix)\"", RegexOptions.IgnoreCase);
                Match m = re.Match(json ?? "");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        private static string GetChecksum(string msixName, string tag, string ghPrefix)
        {
            try
            {
                string sumUrl = ghPrefix + "/" + MirrorRepo + "/releases/download/" + tag + "/SHA256SUMS.txt";
                string tmp = Path.Combine(Path.GetTempPath(), "Codex-SHA256SUMS.txt");
                if (DownloadFile(sumUrl, tmp, "SHA256SUMS.txt", 16))
                {
                    foreach (string line in File.ReadAllLines(tmp))
                    {
                        string[] p = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 2 && p[1].Equals(msixName, StringComparison.OrdinalIgnoreCase))
                        { try { File.Delete(tmp); } catch { } return p[0].ToLower(); }
                    }
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch { }
            return null;
        }

        private static string Sha256File(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (SHA256Managed sha = new SHA256Managed())
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLower();
        }

        private static string GetMsix(string arch, string installedVersion)
        {
            Directory.CreateDirectory(CacheDir);
            Step("2/11 获取镜像仓库最新版本信息");
            string tag = GetGitHubLatestTag(MirrorRepo);
            if (tag == null) throw new Exception("无法获取镜像仓库最新版本");
            Ok("镜像最新 tag: " + tag);
            string msixName = GetMsixAssetName(tag, arch);
            // 版本对比：已装 >= 镜像最新 → 跳过下载（避免重复下载 1GB）
            string latestVer = ExtractVersionFromPackageName(msixName);
            if (!string.IsNullOrEmpty(installedVersion) && !string.IsNullOrEmpty(latestVer))
            {
                Version vi, vl;
                if (Version.TryParse(installedVersion, out vi) && Version.TryParse(latestVer, out vl) && vi >= vl)
                {
                    Ok("Codex 已是最新版本（" + installedVersion + " ≥ 镜像 " + latestVer + "），跳过下载安装");
                    return null;
                }
                Ok("检测到新版本 " + latestVer + "（当前 " + installedVersion + "）→ 下载升级（数据保留）");
            }
            if (string.IsNullOrEmpty(msixName))
                throw new Exception("未在 Release 中找到 " + arch + " 架构的 MSIX 资产");
            Ok("MSIX: " + msixName);

            string cacheFile = Path.Combine(CacheDir, msixName);
            // 缓存命中
            if (!SkipChecksum && File.Exists(cacheFile))
            {
                string cachedSha = Sha256File(cacheFile);
                foreach (string prefix in GhProxyPrefixes)
                {
                    string expected = GetChecksum(msixName, tag, prefix);
                    if (!string.IsNullOrEmpty(expected) && cachedSha == expected)
                    {
                        Ok("缓存命中，跳过下载: " + cacheFile + " (SHA256 匹配)");
                        return cacheFile;
                    }
                }
                Warn("缓存校验不匹配（可能是旧版本），重新下载");
            }

            foreach (string prefix in GhProxyPrefixes)
            {
                string url = prefix + "/" + MirrorRepo + "/releases/download/" + tag + "/" + msixName;
                Step("尝试下载源: " + prefix);
                if (File.Exists(cacheFile)) File.Delete(cacheFile);
                if (!DownloadFile(url, cacheFile, "Codex MSIX (" + arch + ")", 1048576)) continue;
                if (!SkipChecksum)
                {
                    string expected = GetChecksum(msixName, tag, prefix);
                    if (!string.IsNullOrEmpty(expected))
                    {
                        string actual = Sha256File(cacheFile);
                        if (actual != expected)
                        {
                            Fail("SHA256 校验失败: 期望 " + expected + " / 实际 " + actual);
                            File.Delete(cacheFile);
                            continue;
                        }
                        Ok("SHA256 校验通过: " + actual);
                    }
                    else Warn("未获取到期望校验和，跳过校验");
                }
                Ok("已缓存: " + cacheFile);
                return cacheFile;
            }
            throw new Exception("所有下载源均失败，安装中止。");
        }

        // ================= 安装（PowerShell SDK 进程内） =================
        private static List<AppxInfo> GetAppxPackages(string nameFilter)
        {
            List<AppxInfo> list = new List<AppxInfo>();
            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddCommand("Get-AppxPackage");
                if (!string.IsNullOrEmpty(nameFilter)) ps.AddParameter("Name", nameFilter);
                foreach (PSObject r in ps.Invoke())
                {
                    list.Add(new AppxInfo
                    {
                        Name = GetProp(r, "Name"),
                        Version = GetProp(r, "Version"),
                        PackageFamilyName = GetProp(r, "PackageFamilyName"),
                        InstallLocation = GetProp(r, "InstallLocation"),
                        PackageFullName = GetProp(r, "PackageFullName")
                    });
                }
            }
            return list;
        }

        private static AppxInfo GetCodexAppx()
        {
            foreach (AppxInfo i in GetAppxPackages("*Codex*"))
                if (i.Name.IndexOf("ClaudeCode", StringComparison.OrdinalIgnoreCase) < 0) return i;
            return null;
        }

        // 机器级暂存（staged/provisioned）包查询：用户级卸载后包文件通常仍在 WindowsApps，
        // 可免下载直接补注册。Get-AppxPackage -AllUsers 需要管理员——非提权运行时失败返回
        // null（走正常下载安装路径），提权安装器内可用。
        private static AppxInfo GetStagedCodex()
        {
            try
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddScript("Get-AppxPackage -Name '*Codex*' -AllUsers");
                    foreach (PSObject r in ps.Invoke())
                    {
                        string name = GetProp(r, "Name");
                        if (string.IsNullOrEmpty(name) || name.IndexOf("ClaudeCode", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        AppxInfo i = new AppxInfo
                        {
                            Name = name,
                            Version = GetProp(r, "Version"),
                            PackageFamilyName = GetProp(r, "PackageFamilyName"),
                            InstallLocation = GetProp(r, "InstallLocation"),
                            PackageFullName = GetProp(r, "PackageFullName")
                        };
                        if (!string.IsNullOrEmpty(i.PackageFullName) && !string.IsNullOrEmpty(i.InstallLocation)) return i;
                    }
                }
            }
            catch { }
            return null;
        }

        // 从暂存包的 AppxManifest 补注册当前用户（免下载；等价设置页卸载后的快速恢复）
        private static bool RegisterStagedUser(AppxInfo staged)
        {
            try
            {
                if (string.IsNullOrEmpty(staged.InstallLocation)) return false;
                string manifest = Path.Combine(staged.InstallLocation, "AppxManifest.xml");
                if (!File.Exists(manifest)) return false;
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddScript("Add-AppxPackage -Register '" + manifest.Replace("'", "''") + "' -DisableDevelopmentMode");
                    ps.Invoke();
                    if (ps.Streams.Error.Count > 0)
                    {
                        Warn("从暂存补注册当前用户失败: " + Trunc(CollectErrors(ps)));
                        return false;
                    }
                }
                return GetCodexAppx() != null;
            }
            catch (Exception ex) { Warn("从暂存补注册异常: " + Trunc(ex.Message)); return false; }
        }

        private static string GetProp(PSObject obj, string name)
        {
            PSPropertyInfo pr = obj.Properties[name];
            return (pr != null && pr.Value != null) ? pr.Value.ToString() : "";
        }

        private static string CollectErrors(PowerShell ps)
        {
            StringBuilder sb = new StringBuilder();
            foreach (ErrorRecord e in ps.Streams.Error) sb.Append(e.ToString()).Append("; ");
            return sb.ToString();
        }

        private static AppxInfo InstallMsix(string msixPath, string latestVersion)
        {
            Step("安装 MSIX 包");
            AppxInfo existing = GetCodexAppx();
            if (existing != null && !string.IsNullOrEmpty(latestVersion))
            {
                Version ve, vn;
                if (Version.TryParse(existing.Version, out ve) && Version.TryParse(latestVersion, out vn) && ve >= vn)
                {
                    Ok("已检测到 Codex Desktop（版本 " + existing.Version + " ≥ " + latestVersion + "），跳过安装");
                    return existing;
                }
                Ok("覆盖升级到 " + latestVersion + "（当前 " + existing.Version + "，应用数据保留）");
            }
            else if (existing != null)
            {
                Ok("已检测到 Codex Desktop（版本 " + existing.Version + "），跳过安装");
                return existing;
            }
            string err;
            if (TryProvision(msixPath, out err))
            {
                Ok("机器范围注册成功（所有用户可用）");
                // 机器范围注册不注册给当前用户，补用户级注册立即生效
                try
                {
                    if (!AddAppx(msixPath, out err)) Warn("用户级立即注册失败（重新登录后自动部署，可忽略）: " + err);
                    else Ok("用户级立即注册成功（当前会话可用）");
                }
                catch (Exception ex) { Warn("用户级立即注册异常（可忽略）: " + ex.Message); }
            }
            else
            {
                Warn("机器范围注册失败（" + (err.Length > 0 ? err : "未知错误") + "），回退用户级安装 ...");
                string aerr = "";
                if (!AddAppx(msixPath, out aerr))
                {
                    string hint = NoElevate && aerr.IndexOf("管理员", StringComparison.Ordinal) >= 0
                        ? "（提示：此 MSIX 安装需要管理员权限，请去掉 -NoElevate 让程序自动提权）" : "";
                    throw new Exception("Add-AppxPackage 失败: " + aerr + hint);
                }
                Ok("用户级安装成功");
            }
            AppxInfo a = GetCodexAppx();
            if (a == null) a = GetProvisionedAppx();
            if (a == null)
                throw new Exception("安装已完成，但未能检测到 Codex 包（可能需重新登录后生效）。请重新登录后检查。");
            return a;
        }

        private static bool TryProvision(string msixPath, out string err)
        {
            err = "";
            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddCommand("Add-AppxProvisionedPackage")
                  .AddParameter("Online", true)
                  .AddParameter("PackagePath", msixPath)
                  .AddParameter("SkipLicense", true)
                  .AddParameter("Regions", "all");
                try { ps.Invoke(); }
                catch (Exception ex) { err = ex.Message; return false; } // 需要提升等终止性异常
                if (ps.HadErrors) { err = CollectErrors(ps); return false; }
            }
            return true;
        }

        private static bool AddAppx(string msixPath, out string err)
        {
            err = "";
            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddCommand("Add-AppxPackage").AddParameter("Path", msixPath);
                try { ps.Invoke(); }
                catch (Exception ex) { err = ex.Message; return false; }
                if (ps.HadErrors) { err = CollectErrors(ps); return false; }
            }
            return true;
        }

        private static AppxInfo GetProvisionedAppx()
        {
            try
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddCommand("Get-AppxProvisionedPackage").AddParameter("Online", true);
                    foreach (PSObject r in ps.Invoke())
                    {
                        string pn = GetProp(r, "PackageName");
                        if (!string.IsNullOrEmpty(pn) && pn.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return new AppxInfo
                            {
                                Name = "OpenAI.Codex",
                                Version = ExtractVersionFromPackageName(pn),
                                PackageFamilyName = GetProp(r, "PackageFamilyName"),
                                PackageFullName = pn,
                                InstallLocation = GetProp(r, "InstallLocation")
                            };
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string ExtractVersionFromPackageName(string packageName)
        {
            Match m = Regex.Match(packageName, "_((\\d+\\.){3}\\d+)_");
            return m.Success ? m.Groups[1].Value : "";
        }

        // ================= 桌面快捷方式 =================
        private static string GetAumid(AppxInfo appx)
        {
            try
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddCommand("Get-StartApps");
                    foreach (PSObject r in ps.Invoke())
                    {
                        string appId = GetProp(r, "AppID");
                        if (!string.IsNullOrEmpty(appId) && appId.IndexOf(appx.PackageFamilyName, StringComparison.OrdinalIgnoreCase) >= 0)
                            return appId;
                    }
                }
            }
            catch { }
            return appx.PackageFamilyName + "!App";
        }

        private static void CreateDesktopShortcut(AppxInfo appx)
        {
            try
            {
                string aumid = GetAumid(appx);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                // 桌面 + 开始菜单各创建一份（与汉化版快捷方式行为一致）
                foreach (string lnkPath in new string[]
                {
                    Path.Combine(desktop, "Codex.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Codex.lnk")
                })
                {
                    string script =
                        "$ws = New-Object -ComObject WScript.Shell;" +
                        "$sc = $ws.CreateShortcut('" + lnkPath.Replace("'", "''") + "');" +
                        "$sc.TargetPath = 'shell:AppsFolder\\" + aumid + "';" +
                        "$sc.Description = 'OpenAI Codex';" +
                        "$sc.Save()";
                    InvokePsScript(script);
                }
                Ok("已创建桌面/开始菜单快捷方式: " + Path.Combine(desktop, "Codex.lnk"));
            }
            catch (Exception ex) { Warn("创建桌面快捷方式失败: " + ex.Message); }
        }

        // 汉化成功后：快捷方式直接指向补丁副本的 Codex.exe（无需任何启动脚本）
        private static void CreateDesktopShortcutToExe(string exePath)
        {
            try
            {
                string appDir = Path.GetDirectoryName(exePath);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnk = Path.Combine(desktop, "Codex.lnk");
                string e = exePath.Replace("'", "''");
                // 图标：Codex 应用自身图标（包内 256px logo → 多尺寸 ICO）→ Xode logo → 启动器默认
                string iconLoc = null;
                try
                {
                    string pngSrc = null;
                    AppxInfo ax = GetCodexAppx();
                    if (ax != null && !string.IsNullOrEmpty(ax.InstallLocation))
                    {
                        string cand = Path.Combine(ax.InstallLocation, "assets", "Square44x44Logo.targetsize-256_altform-unplated.png");
                        if (File.Exists(cand)) pngSrc = cand;
                    }
                    if (pngSrc == null)
                    {
                        string appExe = null;
                        if (ax != null && !string.IsNullOrEmpty(ax.InstallLocation))
                        {
                            string cand2 = Path.Combine(ax.InstallLocation, "app", "ChatGPT.exe");
                            if (File.Exists(cand2)) appExe = cand2;
                        }
                        if (appExe == null)
                        {
                            string binDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
                            if (Directory.Exists(binDir))
                                foreach (string f in Directory.GetFiles(binDir, "codex.exe", SearchOption.AllDirectories))
                                    { appExe = f; break; }
                        }
                        if (appExe != null)
                        {
                            using (Icon ic = System.Drawing.Icon.ExtractAssociatedIcon(appExe))
                            using (Bitmap bm = ic.ToBitmap())
                            {
                                pngSrc = Path.Combine(Path.GetTempPath(), "codex-app-icon.png");
                                bm.Save(pngSrc, System.Drawing.Imaging.ImageFormat.Png);
                            }
                        }
                    }
                    if (pngSrc != null)
                    {
                        string assetDir = Path.Combine(Path.GetDirectoryName(CacheDir), "assets");
                        Directory.CreateDirectory(assetDir);
                        string appIco = Path.Combine(assetDir, "codex-app.ico");
                        BuildMultiSizeIco(pngSrc, appIco);
                        iconLoc = appIco;
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(iconLoc) && !string.IsNullOrEmpty(XodeLogoIcoPath))
                    iconLoc = XodeLogoIcoPath + ",0";
                string iconLine = string.IsNullOrEmpty(iconLoc)
                    ? "$sc.IconLocation = '" + e + ",0';"
                    : "$sc.IconLocation = '" + iconLoc.Replace("'", "''") + ",0';";
                // 启动包装脚本：先退出 Codex 再经启动器注入并启动（--launch-zh 要求客户端先退出）
                string wrapperPs1 = Path.Combine(appDir, "Xode启动.ps1");
                string wrapper =
                    "Get-Process -Name ChatGPT -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue" + "\r\n" +
                    "Start-Sleep -Seconds 2" + "\r\n" +
                    "Start-Process -FilePath '" + e + "' -ArgumentList '--launch-zh' -WorkingDirectory '" + appDir.Replace("'", "''") + "' -Wait";
                // 必须带 BOM：PowerShell 5.1 对无 BOM 的 .ps1 按 ANSI 代码页解码，
                // 中文用户名（路径含中文）会被读成乱码导致启动器无法启动
                File.WriteAllText(wrapperPs1, wrapper, new UTF8Encoding(true));
                string script =
                    "$ws = New-Object -ComObject WScript.Shell;" +
                    "$sc = $ws.CreateShortcut('" + lnk.Replace("'", "''") + "');" +
                    "$sc.TargetPath = 'powershell.exe';" +
                    "$sc.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + wrapperPs1.Replace("'", "''") + "\"';" +
                    "$sc.WorkingDirectory = '" + appDir.Replace("'", "''") + "';" +
                    iconLine +
                    "$sc.Description = 'Xode AI Codex（中文版）';" +
                    "$sc.Save()";
                InvokePsScript(script);
                // 开始菜单快捷方式：与桌面快捷方式保持一致（同一包装脚本与图标）
                string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Codex.lnk");
                string smScript = script.Replace("CreateShortcut('" + lnk.Replace("'", "''") + "')", "CreateShortcut('" + startMenuLnk.Replace("'", "''") + "')");
                InvokePsScript(smScript);
                Ok("桌面快捷方式已指向汉化版: " + lnk);
                Ok("开始菜单快捷方式已创建: " + startMenuLnk);
                // 刷新图标缓存（桌面图标即时更新）
                try
                {
                    string ie4uinit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ie4uinit.exe");
                    if (File.Exists(ie4uinit)) Process.Start(ie4uinit, "-show");
                }
                catch { }
            }
            catch (Exception ex) { Warn("创建桌面快捷方式失败: " + ex.Message); }
        }

        private static void InvokePsScript(string script)
        {
            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddScript(script);
                ps.Invoke();
                if (ps.HadErrors) throw new Exception(CollectErrors(ps));
            }
        }

        // ================= 聚合 API 配置（手动回退模式） =================
        private static void ConfigureApiManual()
        {
            string baseUrl = ApiBaseUrl != null ? ApiBaseUrl.Trim() : "";
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = DefaultApiBaseUrl;
            string apiKey = ApiKey != null ? ApiKey.Trim() : "";
            string model = ApiModel != null ? ApiModel.Trim() : "";
            if (string.IsNullOrEmpty(apiKey))
            {
                Console.WriteLine();
                Console.Write("是否配置聚合 API（OpenAI Responses 兼容端点）？[Y/n] ");
                string ans = SafeReadLine();
                bool yes = string.IsNullOrWhiteSpace(ans) ||
                           ans.Trim().ToLower() == "y" || ans.Trim().ToLower() == "yes";
                if (yes)
                {
                    Console.Write("  聚合 Base URL [默认 " + DefaultApiBaseUrl + "]: ");
                    string b = SafeReadLine().Trim();
                    if (!string.IsNullOrEmpty(b)) baseUrl = b;
                    Console.Write("  API Key（格式 sk-xxxxxx）: ");
                    apiKey = SafeReadLine().Trim();
                }
            }
            if (string.IsNullOrEmpty(model)) model = DefaultModel;
            if (string.IsNullOrEmpty(apiKey))
            {
                Warn("未提供 API Key，跳过聚合 API 配置（可加 -ApiKey <key> 或稍后重跑）");
                return;
            }
            if (!apiKey.StartsWith("sk-"))
                Warn("提示: API Key 通常以 sk- 开头，请确认格式（示例 sk-xxxxxx）");
            Step("配置聚合 API: " + baseUrl + "，模型: " + model);
            WriteConfigToml(baseUrl, model);
            WriteAuthJson(apiKey);
            Ok("聚合 API 配置完成。请完全退出并重启 Codex 桌面应用生效。");
        }

        // 手动模式旧模板（-ApiKey 回退用）
        private static void WriteConfigToml(string baseUrl, string model)
        {
            string path = CodexHomePath("config.toml");
            BackupFile(path);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Generated by CodexAppInstaller");
            sb.AppendLine("model = \"" + EscapeToml(model) + "\"");
            sb.AppendLine("model_provider = \"custom\"");
            sb.AppendLine("preferred_auth_method = \"apikey\"");
            sb.AppendLine();
            sb.AppendLine("[model_providers.custom]");
            sb.AppendLine("name = \"" + EscapeToml(ProviderDisplayName) + "\"");
            sb.AppendLine("base_url = \"" + EscapeToml(baseUrl) + "\"");
            sb.AppendLine("wire_api = \"responses\"");
            sb.AppendLine("requires_openai_auth = true");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Ok("已写入: " + path);
        }

        // sub2api 官方模板（UseKeyModal api-key 模式）+ 安装器增强项：
        //   web_search=live、Full Access、记忆、关闭更新检查；每个分组一个 provider（首个 = 默认），
        //   其余分组生成 CLI profile（codex --profile g<gid> 可切换）
        private static void WriteSub2apiConfigToml(List<GroupKeyInfo> keys)
        {
            if (keys == null || keys.Count == 0) throw new Exception("无可用分组 Key");
            GroupKeyInfo primary = keys[0];
            string model = string.IsNullOrEmpty(primary.Model) ? DefaultModel : primary.Model;
            string path = CodexHomePath("config.toml");
            BackupFile(path);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Generated by CodexAppInstaller (Xode AI平台)");
            sb.AppendLine("# 默认 provider = " + primary.GroupName + "#" + primary.GroupId
                          + "；切换：codex --profile g<分组id>，或改 model_provider");
            sb.AppendLine("model_provider = \"OpenAI\"");
            sb.AppendLine("model = \"" + EscapeToml(model) + "\"");
            sb.AppendLine("review_model = \"" + EscapeToml(model) + "\"");
            sb.AppendLine("web_search = \"live\"");
            if (!string.IsNullOrEmpty(primary.Effort) && primary.Effort != "none")
                sb.AppendLine("model_reasoning_effort = \"" + EscapeToml(primary.Effort) + "\"");
            sb.AppendLine("check_for_update_on_startup = false");
            sb.AppendLine("approval_policy = \"never\"");
            sb.AppendLine("sandbox_mode = \"danger-full-access\"");
            sb.AppendLine();
            foreach (GroupKeyInfo ki in keys)
            {
                sb.AppendLine("[model_providers." + ki.ProviderId + "]");
                sb.AppendLine("name = \"" + EscapeToml(ProviderDisplayName) + "\"");
                sb.AppendLine("base_url = \"" + EscapeToml(GetSite() + "/v1") + "\"");
                sb.AppendLine("model_catalog_url = \"" + EscapeToml(GetSite() + "/v1/models") + "\"");
                sb.AppendLine("requires_openai_auth = false");
                sb.AppendLine("experimental_bearer_token = \"" + EscapeToml(ki.Key) + "\"");
                sb.AppendLine("http_headers = { \"x-openai-actor-authorization\" = \"local-image-extension\" }");
                sb.AppendLine();
            }
            sb.AppendLine("[features]");
            sb.AppendLine("api_key_model_discovery = true");
            sb.AppendLine("image_generation = true");
            sb.AppendLine("goals = true");
            sb.AppendLine("memories = true");
            foreach (GroupKeyInfo ki in keys)
            {
                if (ki.ProviderId == "OpenAI") continue; // 默认 provider 无需 profile
                string pModel = string.IsNullOrEmpty(ki.Model) ? DefaultModel : ki.Model;
                sb.AppendLine();
                sb.AppendLine("[profiles.g" + ki.GroupId + "]");
                sb.AppendLine("model_provider = \"" + ki.ProviderId + "\"");
                sb.AppendLine("model = \"" + EscapeToml(pModel) + "\"");
                if (!string.IsNullOrEmpty(ki.Effort) && ki.Effort != "none")
                    sb.AppendLine("model_reasoning_effort = \"" + EscapeToml(ki.Effort) + "\"");
            }
            // 保留用户/应用自己的配置段（已装插件登记 [plugins.*]、[mcp_servers.*]、[marketplaces.*]、
            // [desktop] 等），避免重跑安装器抹掉已安装插件与自定义设置。托管段整段重写为最新值。
            string preserved = CollectPreservedTomlBlocks(path);
            if (preserved.Length > 0) sb.AppendLine();
            File.WriteAllText(path, sb.ToString() + preserved, new UTF8Encoding(false)); // 无 BOM：严格 TOML 解析器不接受 BOM
            Ok("已写入 " + keys.Count + " 个分组 provider: " + path + (preserved.Length > 0 ? "（已保留既有插件/自定义配置）" : ""));
        }

        // 安装器托管的顶层键：每次重写为最新值，不从旧文件保留
        private static readonly string[] ManagedTopLevelKeys = new string[] {
            "model_provider", "model", "review_model", "web_search", "model_reasoning_effort",
            "check_for_update_on_startup", "approval_policy", "sandbox_mode",
            // 历史版本写入、桌面端不识别的死键：直接丢弃（消除「is ignored」警告）
            "disable_response_storage", "network_access", "windows_wsl_setup_acknowledged"
        };

        // 安装器托管的 TOML 区块：整段重写，不从旧文件保留
        private static bool IsManagedSection(string header)
        {
            return header.StartsWith("[model_providers.")
                || header.StartsWith("[profiles.g")
                || header == "[features]";
        }

        private static int TomlBracketDelta(string s)
        {
            int d = 0; bool inStr = false;
            foreach (char c in s)
            {
                if (c == '"') inStr = !inStr;
                if (inStr) continue;
                if (c == '[' || c == '{') d++;
                else if (c == ']' || c == '}') d--;
            }
            return d;
        }

        // 从旧 config.toml 提取安装器不管辖的部分（按区块划分：顶层键行 + 各 [section] 块）
        private static string CollectPreservedTomlBlocks(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                StringBuilder outSb = new StringBuilder();
                string curHeader = null;
                List<string> block = new List<string>();
                int depth = 0; bool skipping = false; // 顶层多行值的状态机
                Action flush = delegate
                {
                    if (block.Count == 0) return;
                    // 自愈：跳过历史版本残留的孤立引号行（单独一个 ' 永远不是合法 TOML）
                    for (int i = block.Count - 1; i >= 0; i--)
                    {
                        string tt = block[i].Trim();
                        if (tt.Length > 0 && tt.Trim('\'').Length == 0) block.RemoveAt(i);
                    }
                    if (curHeader == null)
                    {
                        foreach (string l in block)
                        {
                            string t = l.Trim();
                            if (t.Length == 0 || t.StartsWith("#")) { if (!skipping) outSb.AppendLine(l); continue; }
                            int eq = t.IndexOf('=');
                            if (depth == 0 && eq > 0)
                            {
                                string key = t.Substring(0, eq).Trim().Trim('"').Trim('\'').ToLowerInvariant();
                                bool managed = false;
                                foreach (string mk in ManagedTopLevelKeys) if (mk == key) { managed = true; break; }
                                depth = Math.Max(0, TomlBracketDelta(t));
                                if (managed) { skipping = true; continue; }
                                skipping = false;
                                outSb.AppendLine(l);
                                if (depth > 0) skipping = false; // 未托管键的续行保留
                            }
                            else
                            {
                                depth += TomlBracketDelta(t);
                                if (!skipping) outSb.AppendLine(l);
                                if (depth <= 0) { depth = 0; skipping = false; }
                            }
                        }
                    }
                    else if (!IsManagedSection(curHeader))
                    {
                        foreach (string l in block) outSb.AppendLine(l);
                    }
                    block.Clear();
                };
                foreach (string line in lines)
                {
                    string t = line.Trim();
                    if (depth == 0 && t.StartsWith("[") && t.EndsWith("]"))
                    {
                        flush();
                        curHeader = t;
                        block.Add(line);
                        continue;
                    }
                    block.Add(line);
                }
                flush();
                return outSb.ToString();
            }
            catch (Exception ex) { Warn("保留旧配置段失败（已忽略，配置将整文件重写）: " + Trunc(ex.Message)); return ""; }
        }

        // ~/.codex/auth.json 存 API Key（保险项：sub2api api-key 模式密钥内联于 config.toml，
        // 此文件供回退/兼容使用；桌面端从桌面启动不读 shell 环境变量，auth.json 最可靠）
        private static void WriteAuthJson(string apiKey)
        {
            string path = CodexHomePath("auth.json");
            string json = "{\n  \"OPENAI_API_KEY\": \"" + EscapeJson(apiKey) + "\"\n}\n";
            File.WriteAllText(path, json, new UTF8Encoding(false));
            Ok("已写入 API Key: " + path);
        }

        // 预置桌面设置页「完全访问权限」开关的底层状态（.codex-global-state.json 的
        // electron-owner-atoms:signed-out / permission-selection-by-host-id:local = {"kind":"full-access"}）。
        // 文件已存在则合并保留其余键；不存在则创建最小结构（应用首启会补全其余默认值）。
        private static void EnsureFullAccessDefault()
        {
            try
            {
                string path = CodexHomePath(".codex-global-state.json");
                Dictionary<string, object> root;
                if (File.Exists(path))
                {
                    root = ParseJson(File.ReadAllText(path, Encoding.UTF8));
                    if (root == null) { Warn("全局状态文件解析失败，跳过预置完全访问权限"); return; }
                }
                else
                {
                    root = new Dictionary<string, object>();
                }
                object atomsObj;
                Dictionary<string, object> atoms;
                if (!root.TryGetValue("electron-owner-atoms:signed-out", out atomsObj) || !(atomsObj is Dictionary<string, object>))
                {
                    atoms = new Dictionary<string, object>();
                    root["electron-owner-atoms:signed-out"] = atoms;
                }
                else
                {
                    atoms = (Dictionary<string, object>)atomsObj;
                }
                object selObj;
                Dictionary<string, object> sel;
                if (atoms.TryGetValue("permission-selection-by-host-id:local", out selObj) && selObj is Dictionary<string, object>)
                {
                    sel = (Dictionary<string, object>)selObj;
                }
                else
                {
                    sel = new Dictionary<string, object>();
                    atoms["permission-selection-by-host-id:local"] = sel;
                }
                sel["kind"] = "full-access";
                JavaScriptSerializer jss = new JavaScriptSerializer();
                File.WriteAllText(path, jss.Serialize(root), new UTF8Encoding(false));
                Ok("已预置桌面默认权限: 完全访问权限（.codex-global-state.json）");
            }
            catch (Exception ex)
            {
                Warn("预置完全访问权限失败（可在应用 设置→常规→权限 中手动打开一次）: " + Trunc(ex.Message));
            }
        }

        // ================= Xode AI 品牌化（桌面/任务栏/启动器图标） =================
        // （XodeLogoIcoPath / XodeBrandingOk 字段声明在类头部）
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);

        // 寻找 logo：-Logo 参数 > 安装器同目录 xode-logo.ico / xode-logo.png
        private static string FindLogoSource()
        {
            foreach (string cand in new string[] { XodeLogoParam })
                if (!string.IsNullOrEmpty(cand) && File.Exists(cand)) return cand;
            string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            foreach (string name in new string[] { "xode-logo.ico", "xode-logo.png" })
            {
                string c = Path.Combine(exeDir, name);
                if (File.Exists(c)) return c;
            }
            return null;
        }

        // PNG → 多尺寸 ICO（16/32/48/256，32bpp ARGB + 全零 AND 掩码）
        private static void BuildMultiSizeIco(string pngPath, string icoPath)
        {
            int[] sizes = new int[] { 16, 32, 48, 256 };
            byte[][] imgs = new byte[sizes.Length][];
            using (Bitmap src = new Bitmap(pngPath))
            {
                for (int i = 0; i < sizes.Length; i++)
                    imgs[i] = BmpToIcoImage(ResizeSquare(src, sizes[i]));
            }
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ms);
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int s = sizes[i];
                    w.Write((byte)(s == 256 ? 0 : s));
                    w.Write((byte)(s == 256 ? 0 : s));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32);
                    w.Write((uint)imgs[i].Length); w.Write((uint)offset);
                    offset += imgs[i].Length;
                }
                foreach (byte[] img in imgs) w.Write(img);
                File.WriteAllBytes(icoPath, ms.ToArray());
            }
        }

        private static Bitmap ResizeSquare(Bitmap src, int size)
        {
            Bitmap b = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, size, size);
            }
            return b;
        }

        // Bitmap → ICO 单图象数据（40 字节 BITMAPINFOHEADER + 自底向上 BGRA + 全零 AND 掩码）
        private static byte[] BmpToIcoImage(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] px = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length);
            bmp.UnlockBits(bd);
            byte[] pxFlip = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Array.Copy(px, (h - 1 - y) * w * 4, pxFlip, y * w * 4, w * 4);
            int maskRow = ((w + 31) / 32) * 4;
            byte[] img = new byte[40 + pxFlip.Length + maskRow * h];
            WriteInt32(img, 0, 40);
            WriteInt32(img, 4, w);
            WriteInt32(img, 8, h * 2);
            WriteInt16(img, 12, 1);
            WriteInt16(img, 14, 32);
            Array.Copy(pxFlip, 0, img, 40, pxFlip.Length);
            return img;
        }

        private static void WriteInt32(byte[] b, int off, int v)
        {
            b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
        }

        private static void WriteInt16(byte[] b, int off, int v)
        {
            b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
        }

        // 解析 .ico → 各图象数据（用于 exe 图标资源替换）
        private static byte[][] ReadIcoImages(string icoPath, out int[] sizes)
        {
            byte[] ico = File.ReadAllBytes(icoPath);
            ushort count = BitConverter.ToUInt16(ico, 4);
            byte[][] r = new byte[count][];
            sizes = new int[count];
            for (int i = 0; i < count; i++)
            {
                int e = 6 + 16 * i;
                sizes[i] = ico[e] == 0 ? 256 : ico[e];
                int len = BitConverter.ToInt32(ico, e + 8);
                int off = BitConverter.ToInt32(ico, e + 12);
                r[i] = new byte[len];
                Array.Copy(ico, off, r[i], 0, len);
            }
            return r;
        }

        // 替换启动器 exe 的图标资源（RT_ICON 1..N + RT_GROUP_ICON 1）
        private static void PatchLauncherIcon()
        {
            string launcherExe = Path.Combine(Path.GetDirectoryName(CacheDir), "zh-launcher", "Codex-Zh-Launcher-Windows-x64.exe");
            if (!File.Exists(launcherExe)) { Warn("未找到启动器 exe，跳过其图标替换"); return; }
            // 启动器若在运行会锁定 exe（资源提交拒绝），先结束
            foreach (Process p in Process.GetProcessesByName("Codex-Zh-Launcher-Windows-x64"))
                { try { p.Kill(); p.WaitForExit(5000); } catch { } }
            int[] icoSizes;
            byte[][] imgs = ReadIcoImages(XodeLogoIcoPath, out icoSizes);
            IntPtr h = BeginUpdateResource(launcherExe, false);
            if (h == IntPtr.Zero) throw new Exception("BeginUpdateResource 失败（错误 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "）");
            try
            {
                for (int i = 0; i < imgs.Length; i++)
                    if (!UpdateResource(h, (IntPtr)3, (IntPtr)(i + 1), 0, imgs[i], (uint)imgs[i].Length))
                        throw new Exception("UpdateResource RT_ICON " + (i + 1) + " 失败");
                // 重建图标组（4 项，id 1..4）
                byte[] grp = new byte[6 + 14 * imgs.Length];
                WriteInt16(grp, 0, 0); WriteInt16(grp, 2, 1); WriteInt16(grp, 4, (ushort)imgs.Length);
                for (int i = 0; i < imgs.Length; i++)
                {
                    int e = 6 + 14 * i;
                    int s = icoSizes[i];
                    grp[e] = (byte)(s == 256 ? 0 : s);
                    grp[e + 1] = (byte)(s == 256 ? 0 : s);
                    grp[e + 2] = 0; grp[e + 3] = 0;
                    WriteInt16(grp, e + 4, 1); WriteInt16(grp, e + 6, 32);
                    WriteInt32(grp, e + 8, imgs[i].Length);
                    WriteInt16(grp, e + 12, (ushort)(i + 1));
                    WriteInt16(grp, e + 12, (ushort)(i + 1));
                }
                if (!UpdateResource(h, (IntPtr)14, (IntPtr)1, 0, grp, (uint)grp.Length))
                    throw new Exception("UpdateResource RT_GROUP_ICON 失败");
                if (!EndUpdateResource(h, false)) throw new Exception("EndUpdateResource 失败（错误 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "）");
                Ok("启动器图标已替换为 Xode AI");
            }
            catch
            {
                EndUpdateResource(h, true);
                throw;
            }
        }

        // 品牌化入口：logo → ico → 启动器图标（桌面快捷方式图标在快捷方式步骤引用 ico）
        private static void ApplyXodeBranding()
        {
            try
            {
                string logoSrc = FindLogoSource();
                if (logoSrc == null) { Ok("未提供 Xode AI logo（安装器同目录 xode-logo.ico/png），跳过品牌化"); return; }
                Ok("Xode AI 品牌化: 使用 logo " + logoSrc);
                string assetDir = Path.Combine(Path.GetDirectoryName(CacheDir), "assets");
                Directory.CreateDirectory(assetDir);
                string icoPath = Path.Combine(assetDir, "xode-logo.ico");
                if (logoSrc.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Compare(logoSrc, icoPath, StringComparison.OrdinalIgnoreCase) != 0) File.Copy(logoSrc, icoPath, true);
                }
                else BuildMultiSizeIco(logoSrc, icoPath);
                XodeLogoIcoPath = icoPath;
                Ok("Xode AI 图标已生成: " + icoPath);
                PatchLauncherIcon();
                XodeBrandingOk = true;
            }
            catch (Exception ex) { Warn("Xode AI 品牌化失败（不影响其他功能）: " + Trunc(ex.Message) + " | " + Trunc((ex.StackTrace ?? "").Replace("\r\n", " <- "))); }

        }

        // ================= 插件市场（中国内地直连） =================
// 中国社区维护的「Codex 官方插件 · 中文版」市场（kael-odin/codex-plugins-zh）：
// 192 个官方插件、中文分类、i18n/zh 覆盖层，社区持续更新。
// 注册为本地目录市场（config.toml 的 [marketplaces.<id>]，与官方 CLI 一致），安装插件零外网依赖。
        // 插件市场：两个来源合成一个统一市场「Codex 插件」
        //   基础 = kael-odin/codex-plugins-zh（openai/plugins 官方市场无损汉化镜像）
        //   增补 = 暴喵市场精选插件（BaoMiaoPickedPlugins）
        // 版本管理：运行时查询各仓库最新 commit，与本地已装版本（mp\<prefix>.version）比对——
        //   版本相同 → 完全跳过下载/解压/合并；版本变化或首次安装 → 下载换装并记录新版本
        private class MarketSpec
        {
            public string Repo, Branch, CachePrefix, SizeDesc;
            public long MinSize;
            public string BootstrapCommit;   // 查询 API 失败且本地无版本记录时的兜底版本
            public string ResolvedCommit;    // 运行时解析出的目标 commit
        }
        private static readonly MarketSpec[] Markets = new MarketSpec[] {
            new MarketSpec { Repo = "kael-odin/codex-plugins-zh", Branch = "main", CachePrefix = "kzh", SizeDesc = "约 72 MB", MinSize = 50000000, BootstrapCommit = "e3091d1b371a9dcd9455d333175f948b286fdfcf" },
            new MarketSpec { Repo = "tetexu1994-star/baomiao-codex-skills-marketplace", Branch = "main", CachePrefix = "baomiao", SizeDesc = "约 4.5 MB", MinSize = 2000000, BootstrapCommit = "cb69c4a7e42ddb85227e6f61cc9eb7bf9e1b7df5" }
        };
        private const string MergedMarketId = "codex-plugins";
        private const string MergedMarketDisplay = "Codex 插件";
        // 从暴喵市场并入的精选插件（name = 暴喵 marketplace.json 的插件 name 字段）
        private static readonly string[] BaoMiaoPickedPlugins = new string[] { "academic-research-toolkit" };
        // 精选插件补充的 ASCII 检索关键词：应用搜索规范化会丢弃全部非 [a-z0-9] 字符
        // （中文查询退化为空串 → 匹配一切），ASCII 关键词让英文检索可精准命中
        private static readonly string[] PickedExtraKeywords = new string[] { "academic", "research", "paper", "literature", "toolkit" };
        private static bool PluginMarketplaceOk;

        private static bool EnsurePluginMarketplace()
        {
            try
            {
                string configPath = CodexHomePath("config.toml");
                string cfg = File.Exists(configPath) ? File.ReadAllText(configPath, Encoding.UTF8) : "";
                // 清理旧方案市场注册（awesome 国际版 / 此前的双市场布局 → 统一为合并市场 codex-plugins）
                // 注意：source 的闭合引号必须一并消费，否则留下孤立 ' 行破坏 TOML 解析
                foreach (string oldId in new string[] { "awesome-codex-plugins", "openai-curated-zh", "a-openai-curated-zh", "baomiao-codex" })
                    cfg = Regex.Replace(cfg, "\r?\n?\\[marketplaces\\." + Regex.Escape(oldId) + "\\]\r?\n?source_type = \"local\"\r?\n?source = '[^'\r\n]*'", "");
                Step("8/11 插件市场（中国内地直连 · 自动更新 · " + MergedMarketDisplay + "）");
                Directory.CreateDirectory(CacheDir);
                string mpRoot = Path.Combine(Path.GetDirectoryName(CacheDir), "mp");
                Directory.CreateDirectory(mpRoot);
                // 清理旧方案遗留目录（旧分支归档布局）
                foreach (string oldDir in new string[] { Path.Combine(mpRoot, "codex-plugins-zh-main"), Path.Combine(mpRoot, "awesome-codex-plugins-main") })
                    if (Directory.Exists(oldDir)) { try { Directory.Delete(oldDir, true); } catch { } }
                string tarExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
                int okCount = 0;
                foreach (MarketSpec m in Markets)
                {
                    try
                    {
                        string localVer = ReadMarketVersion(mpRoot, m.CachePrefix);
                        string target = GetLatestCommit(m.Repo, m.Branch);
                        if (string.IsNullOrEmpty(target))
                        {
                            target = !string.IsNullOrEmpty(localVer) ? localVer : m.BootstrapCommit;
                            Warn("查询市场最新版本失败，使用" + (!string.IsNullOrEmpty(localVer) ? "已装版本 " + target.Substring(0, 8) : "内置兜底版本 " + target.Substring(0, 8)));
                        }
                        m.ResolvedCommit = target;
                        bool versionChanged = (target != localVer) || (localVer == null);
                        EnsureMarketplaceSource(m, mpRoot, tarExe, versionChanged);
                        okCount++;
                    }
                    catch (Exception mex) { Warn("插件市场来源 " + m.CachePrefix + " 获取失败: " + Trunc(mex.Message)); }
                }
                if (okCount == 0) throw new Exception("全部市场来源获取失败");
                // 版本升级/首次安装后清理旧 commit 的市场目录与缓存 zip
                foreach (MarketSpec m in Markets)
                {
                    if (string.IsNullOrEmpty(m.ResolvedCommit)) continue;
                    string repoBase = m.Repo.Substring(m.Repo.IndexOf('/') + 1);
                    string keepDir = Path.Combine(mpRoot, repoBase + "-" + m.ResolvedCommit);
                    foreach (string d in Directory.GetDirectories(mpRoot, repoBase + "-*"))
                        if (string.Compare(d, keepDir, StringComparison.OrdinalIgnoreCase) != 0) { try { Directory.Delete(d, true); Ok("已清理旧版本市场目录: " + Path.GetFileName(d)); } catch { } }
                    string keepZip = Path.Combine(CacheDir, m.CachePrefix + "-" + m.ResolvedCommit.Substring(0, 8) + ".zip");
                    foreach (string z in Directory.GetFiles(CacheDir, m.CachePrefix + "-*.zip"))
                        if (string.Compare(z, keepZip, StringComparison.OrdinalIgnoreCase) != 0) { try { File.Delete(z); Ok("已清理旧版本缓存: " + Path.GetFileName(z)); } catch { } }
                }
                PluginMarketplaceOk = BuildMergedMarketplace(mpRoot, configPath, ref cfg);
                return PluginMarketplaceOk;
            }
            catch (Exception ex)
            {
                Warn("插件市场安装失败（不影响其他功能）: " + Trunc(ex.Message));
                return false;
            }
        }

        // 查询仓库分支最新 commit（GitHub API：直连 + gh-proxy 回退），失败返回 null
        private static string GetLatestCommit(string repo, string branch)
        {
            try
            {
                string json = GetGitHubApiString("repos/" + repo + "/commits/" + branch);
                if (string.IsNullOrEmpty(json)) return null;
                Dictionary<string, object> o = ParseJson(json);
                string sha = JS(o, "sha");
                return (!string.IsNullOrEmpty(sha) && sha.Length >= 40) ? sha : null;
            }
            catch { return null; }
        }

        // 本地版本记录：mp\<CachePrefix>.version（内容 = 完整 commit）
        private static string MarketVersionPath(string mpRoot, string cachePrefix)
        {
            return Path.Combine(mpRoot, cachePrefix + ".version");
        }
        private static string ReadMarketVersion(string mpRoot, string cachePrefix)
        {
            try
            {
                string p = MarketVersionPath(mpRoot, cachePrefix);
                if (!File.Exists(p)) return null;
                string v = File.ReadAllText(p, Encoding.UTF8).Trim();
                return (v.Length >= 40) ? v : null;
            }
            catch { return null; }
        }
        private static void WriteMarketVersion(string mpRoot, string cachePrefix, string commit)
        {
            try { File.WriteAllText(MarketVersionPath(mpRoot, cachePrefix), commit + "\r\n", new UTF8Encoding(false)); }
            catch (Exception ex) { Warn("市场版本记录写入失败: " + Trunc(ex.Message)); }
        }

        // 单个来源市场：版本未变 → 全跳过；版本变化/首次 → 下载（原子写入 + zip 完整性校验）→ tar 解压 → 记录版本
        private static void EnsureMarketplaceSource(MarketSpec m, string mpRoot, string tarExe, bool versionChanged)
        {
            string repoBase = m.Repo.Substring(m.Repo.IndexOf('/') + 1);
            // GitHub commit 归档根目录 = <仓库名>-<完整commit>
            string marketDir = Path.Combine(mpRoot, repoBase + "-" + m.ResolvedCommit);
            string mj = Path.Combine(marketDir, ".agents", "plugins", "marketplace.json");
            if (!versionChanged && File.Exists(mj))
            {
                Ok("市场版本未变化（" + m.CachePrefix + " " + m.ResolvedCommit.Substring(0, 8) + "），跳过下载与解压");
                return;
            }
            string zipPath = Path.Combine(CacheDir, m.CachePrefix + "-" + m.ResolvedCommit.Substring(0, 8) + ".zip");
            // 旧目标版本的缓存 zip 不复用（内容对应旧 commit）
            if (File.Exists(zipPath) && !IsValidZip(zipPath))
            {
                Warn("市场归档缓存损坏，删除重下: " + Path.GetFileName(zipPath));
                try { File.Delete(zipPath); } catch { }
            }
            if (!File.Exists(zipPath))
            {
                bool dl = false;
                foreach (string prefix in ZhCnSources())
                {
                    string url = prefix + "/" + m.Repo + "/archive/" + m.ResolvedCommit + ".zip";
                    Step("尝试下载源: " + prefix);
                    if (DownloadFile(url, zipPath, "插件市场来源 " + m.CachePrefix + "（" + m.SizeDesc + "）", m.MinSize) && IsValidZip(zipPath)) { dl = true; break; }
                }
                if (!dl) throw new Exception("所有下载源均失败");
            }
            if (!File.Exists(mj))
            {
                // 残留的旧解压目录先清掉再解压
                if (Directory.Exists(marketDir)) { try { Directory.Delete(marketDir, true); } catch { } }
                // 用系统内置 tar.exe 解压：仓库含超长嵌套路径，.NET ZipFile/路径 API 会失败，tar 原生支持长路径
                ProcessStartInfo tpi = new ProcessStartInfo(tarExe, "-xf \"" + zipPath + "\" -C \"" + mpRoot + "\"");
                tpi.UseShellExecute = false;
                tpi.CreateNoWindow = true;
                tpi.RedirectStandardOutput = true;
                tpi.RedirectStandardError = true;
                using (Process tp = Process.Start(tpi))
                {
                    string tout = tp.StandardOutput.ReadToEnd();
                    string terr = tp.StandardError.ReadToEnd();
                    tp.WaitForExit();
                    if (tout.Trim().Length > 0) Console.WriteLine(tout.TrimEnd());
                    if (terr.Trim().Length > 0) Console.WriteLine(terr.TrimEnd());
                    if (tp.ExitCode != 0) Warn("tar 有跳过条目（退出码 " + tp.ExitCode + "），以 marketplace.json 是否生成判定成败");
                }
                // 官方中文版仓库带约 35MB 网页资源（site/），Codex 用不到，解压后删除省磁盘
                string siteDir = Path.Combine(marketDir, "site");
                if (Directory.Exists(siteDir)) { try { Directory.Delete(siteDir, true); } catch { } }
                if (!File.Exists(mj)) { try { File.Delete(zipPath); } catch { } throw new Exception("解压后未找到 marketplace.json"); }
            }
            WriteMarketVersion(mpRoot, m.CachePrefix, m.ResolvedCommit);
            Ok("市场来源就绪: " + m.CachePrefix + " @ " + m.ResolvedCommit.Substring(0, 8) + (versionChanged ? "（已更新）" : "（已恢复）"));
        }

        // zip 完整性校验：残缺文件（下载中断）没有中央目录，OpenRead 会失败
        private static bool IsValidZip(string path)
        {
            try { using (ZipArchive za = ZipFile.OpenRead(path)) return za.Entries.Count > 0; }
            catch { return false; }
        }

        // 合成统一市场「Codex 插件」：官方中文版全量插件 + 暴喵精选插件（BaoMiaoPickedPlugins）
        // 版本未变化且清单/注册均已是目标状态 → 全跳过（不写任何文件）
        private static bool BuildMergedMarketplace(string mpRoot, string configPath, ref string cfg)
        {
            try
            {
                MarketSpec baseM = Markets[0], extraM = Markets[1];
                string baseDir = Path.Combine(mpRoot, baseM.Repo.Substring(baseM.Repo.IndexOf('/') + 1) + "-" + baseM.ResolvedCommit);
                string extraDir = Path.Combine(mpRoot, extraM.Repo.Substring(extraM.Repo.IndexOf('/') + 1) + "-" + extraM.ResolvedCommit);
                string mj = Path.Combine(baseDir, ".agents", "plugins", "marketplace.json");
                string extraMj = Path.Combine(extraDir, ".agents", "plugins", "marketplace.json");
                if (!File.Exists(mj) || !File.Exists(extraMj)) throw new Exception("来源市场清单缺失");
                Dictionary<string, object> mktRoot = ParseJson(File.ReadAllText(mj, Encoding.UTF8));
                // 快路径：清单已是合并态（名称/显示名/精选插件齐备）且注册指向当前目录 → 不写任何文件
                bool manifestMerged = (JS(mktRoot, "name") == MergedMarketId)
                    && (JS(JD(mktRoot, "interface") as Dictionary<string, object>, "displayName") == MergedMarketDisplay);
                if (manifestMerged)
                {
                    List<Dictionary<string, object>> cur = AsDictList(JD(mktRoot, "plugins"));
                    foreach (string pick in BaoMiaoPickedPlugins)
                        if (cur.Find(delegate(Dictionary<string, object> e) { return JS(e, "name") == pick; }) == null) { manifestMerged = false; break; }
                }
                string marker = "[marketplaces." + MergedMarketId + "]";
                bool regCurrent = cfg.Contains(marker) && cfg.Contains("source = '" + baseDir + "'");
                if (manifestMerged && regCurrent)
                {
                    Ok("插件市场已是最新且未变化（" + MergedMarketDisplay + "，跳过合并与注册）");
                    return true;
                }
                Dictionary<string, object> extraRoot = ParseJson(File.ReadAllText(extraMj, Encoding.UTF8));
                List<Dictionary<string, object>> extraAll = AsDictList(JD(extraRoot, "plugins"));
                // 基础市场插件清单（JSON 数组 → ArrayList，兼容其他数组类型）
                ArrayList pluginList = JD(mktRoot, "plugins") as ArrayList;
                if (pluginList == null)
                {
                    pluginList = new ArrayList();
                    foreach (object o in (System.Collections.IEnumerable)JD(mktRoot, "plugins")) pluginList.Add(o);
                    mktRoot["plugins"] = pluginList;
                }
                int added = 0;
                foreach (string pick in BaoMiaoPickedPlugins)
                {
                    Dictionary<string, object> entry = null;
                    foreach (Dictionary<string, object> e in extraAll)
                        if (JS(e, "name") == pick) { entry = e; break; }
                    if (entry == null) { Warn("暴喵市场未找到精选插件 " + pick + "，跳过"); continue; }
                    int existingIdx = -1;
                    for (int idx = 0; idx < pluginList.Count; idx++)
                    {
                        Dictionary<string, object> e = pluginList[idx] as Dictionary<string, object>;
                        if (e != null && JS(e, "name") == pick) { existingIdx = idx; break; }
                    }
                    // 复制插件目录（相对路径 ./plugins/<name> 在合并市场中保持不变）+ 补 ASCII 关键词
                    string srcDir = Path.Combine(extraDir, "plugins", pick);
                    string dstDir = Path.Combine(baseDir, "plugins", pick);
                    if (!Directory.Exists(dstDir) && Directory.Exists(srcDir)) CopyDir(srcDir, dstDir);
                    PatchPickedPluginJson(dstDir);
                    if (existingIdx >= 0)
                    {
                        // 已在清单中：移到最前（应用列表按 manifest 顺序渲染）
                        Dictionary<string, object> e = pluginList[existingIdx] as Dictionary<string, object>;
                        pluginList.RemoveAt(existingIdx);
                        pluginList.Insert(0, e);
                    }
                    else
                    {
                        // 插入到清单最前：应用列表按 manifest 顺序渲染，精选插件免搜索直接可见
                        pluginList.Insert(0, entry);
                        added++;
                    }
                }
                // 市场名：Codex 插件
                mktRoot["name"] = MergedMarketId;
                Dictionary<string, object> ifc = JD(mktRoot, "interface") as Dictionary<string, object>;
                if (ifc == null) { ifc = new Dictionary<string, object>(); mktRoot["interface"] = ifc; }
                ifc["displayName"] = MergedMarketDisplay;
                File.WriteAllText(mj, new JavaScriptSerializer().Serialize(mktRoot), new UTF8Encoding(false));
                // 注册/刷新（单市场；commit 更新后 source 路径自动修正）——闭合引号一并移除
                cfg = Regex.Replace(cfg, "\r?\n?\\[marketplaces\\." + Regex.Escape(MergedMarketId) + "\\]\r?\n?source_type = \"local\"\r?\n?source = '[^'\r\n]*'", "");
                StringBuilder sb = new StringBuilder();
                if (cfg.Length > 0 && !cfg.EndsWith("\n")) sb.AppendLine();
                sb.AppendLine(marker);
                sb.AppendLine("source_type = \"local\"");
                sb.AppendLine("source = '" + baseDir + "'");
                File.WriteAllText(configPath, cfg + "\r\n" + sb.ToString(), new UTF8Encoding(false));
                cfg = File.ReadAllText(configPath, Encoding.UTF8);
                Ok("已注册: " + MergedMarketDisplay + "（" + pluginList.Count + " 个插件，国内直连" + (added > 0 ? "，并入暴喵精选 " + added + " 个" : "") + (manifestMerged ? "，清单未变化" : "") + "）");
                return true;
            }
            catch (Exception ex)
            {
                Warn("合并市场构建失败: " + Trunc(ex.Message));
                return false;
            }
        }

        // 递归复制目录
        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        // 给并入的精选插件补充 ASCII 检索关键词（.codex-plugin/plugin.json 的 keywords 数组）
        private static void PatchPickedPluginJson(string pluginDir)
        {
            try
            {
                string pj = Path.Combine(pluginDir, ".codex-plugin", "plugin.json");
                if (!File.Exists(pj)) return;
                Dictionary<string, object> root = ParseJson(File.ReadAllText(pj, Encoding.UTF8));
                ArrayList kw = JD(root, "keywords") as ArrayList;
                if (kw == null)
                {
                    kw = new ArrayList();
                    object o = root.ContainsKey("keywords") ? root["keywords"] : null;
                    if (o is System.Collections.IEnumerable && !(o is string))
                        foreach (object x in (System.Collections.IEnumerable)o) kw.Add(x);
                    root["keywords"] = kw;
                }
                foreach (string k in PickedExtraKeywords)
                    if (!kw.Contains(k)) kw.Add(k);
                File.WriteAllText(pj, new JavaScriptSerializer().Serialize(root), new UTF8Encoding(false));
            }
            catch (Exception ex) { Warn("精选插件元数据补充失败（不影响安装）: " + Trunc(ex.Message)); }
        }

        private static string CodexHomePath(string fileName)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dir = Path.Combine(home, ".codex");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        private static void BackupFile(string path)
        {
            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", true); Ok("已备份原配置: " + Path.GetFileName(path) + ".bak"); }
                catch { }
            }
        }

        private static string EscapeToml(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ================= 汉化（codex-zh-CN，国内线路下载） =================
        private static string[] ZhCnSources()
        {
            string[] s = new string[GhProxyPrefixes.Length + 1];
            GhProxyPrefixes.CopyTo(s, 0);
            s[s.Length - 1] = "https://github.com";
            return s;
        }

        // ================= 汉化（双轨） =================
        // 返回快捷方式目标（新架构=汉化启动器 exe；旧架构=汉化补丁副本 Codex.exe）；失败返回 null（不阻断安装）
        private static string LocalizeCodex(AppxInfo appx)
        {
            try
            {
                string appDir = (appx != null && !string.IsNullOrEmpty(appx.InstallLocation))
                    ? Path.Combine(appx.InstallLocation, "app") : "";
                if (!string.IsNullOrEmpty(appDir) && File.Exists(Path.Combine(appDir, "ChatGPT.exe")))
                {
                    Ok("检测到新版架构（GUI: ChatGPT.exe）→ 使用汉化启动器（运行时注入官方 i18n，不改官方文件）");
                    return LocalizeNewArch();
                }
                return LocalizeLegacy(appx);
            }
            catch (Exception ex)
            {
                Fail("汉化失败: " + Trunc(ex.Message) + "（不影响主体安装）");
                return null;
            }
        }

        // 新架构（GUI=ChatGPT.exe）：下载汉化启动器（国内线路 + SHA256 校验）+ 持久化界面语言
        private static string LocalizeNewArch()
        {
            Directory.CreateDirectory(CacheDir);
            Step("6/11 获取汉化启动器（" + ZhLauncherRepo + "）");
            string tag = GetGitHubLatestTag(ZhLauncherRepo);
            if (tag == null) { tag = ZhLauncherPinTag; Warn("无法获取最新版本号，使用固定版本 " + tag); }
            Ok("已自动检索最新启动器版本: " + tag);
            string launcherDir = Path.Combine(Path.GetDirectoryName(CacheDir), "zh-launcher");
            Directory.CreateDirectory(launcherDir);
            string exePath = Path.Combine(launcherDir, ZhLauncherAsset);
            // 版本对比：本地 exe 版本 >= 最新 release → 跳过下载
            bool needDownload = true;
            if (File.Exists(exePath) && new FileInfo(exePath).Length > 100000)
            {
                Version lv = null;
                try { Version.TryParse(FileVersionInfo.GetVersionInfo(exePath).FileVersion, out lv); } catch { }
                Version tv = null;
                Version.TryParse((tag ?? "").TrimStart('v'), out tv);
                if (lv != null && tv != null && lv >= tv)
                {
                    Ok("启动器已是最新版本（" + lv + "），跳过下载");
                    needDownload = false;
                }
                else if (lv != null && tv != null) Ok("启动器有新版本 " + tv + "（当前 " + lv + "）→ 更新");
            }
            if (needDownload)
            {
                bool dl = false;
                foreach (string prefix in ZhCnSources())
                {
                    string url = prefix + "/" + ZhLauncherRepo + "/releases/download/" + tag + "/" + ZhLauncherAsset;
                    Step("尝试下载源: " + prefix);
                    if (!DownloadFile(url, exePath, "Codex 汉化启动器", 100000)) continue;
                    if (SkipChecksum || VerifyChecksum(exePath, ZhLauncherRepo, tag, ZhLauncherAsset, prefix)) { dl = true; break; }
                    try { File.Delete(exePath); } catch { }
                }
                if (!dl) throw new Exception("汉化启动器所有下载源均失败");
            }
            Step("7/11 写入界面语言配置");
            EnsureDesktopLocaleZhCN();
            Ok("汉化启动器就绪: " + exePath);
            return exePath;
        }

        // 校验下载文件 SHA256（对齐 release 的 SHA256SUMS.txt）
        private static bool VerifyChecksum(string filePath, string repo, string tag, string assetName, string ghPrefix)
        {
            try
            {
                string sumUrl = ghPrefix + "/" + repo + "/releases/download/" + tag + "/SHA256SUMS.txt";
                string tmp = Path.Combine(Path.GetTempPath(), "zh-launcher-SHA256SUMS.txt");
                if (!DownloadFile(sumUrl, tmp, "SHA256SUMS.txt", 16)) return false;
                string expected = null;
                foreach (string line in File.ReadAllLines(tmp))
                {
                    string[] p = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 2 && p[1].Equals(assetName, StringComparison.OrdinalIgnoreCase)) { expected = p[0].ToLower(); break; }
                }
                try { File.Delete(tmp); } catch { }
                if (string.IsNullOrEmpty(expected)) return false;
                string actual = Sha256File(filePath);
                if (actual != expected) { Fail("SHA256 校验失败: 期望 " + expected + " / 实际 " + actual); return false; }
                Ok("SHA256 校验通过");
                return true;
            }
            catch { return false; }
        }

        // config.toml 写入/更新 [desktop] localeOverride="zh-CN"（等价 codex-zh-CN 的 setCodexLocale）
        private static void EnsureDesktopLocaleZhCN()
        {
            string path = CodexHomePath("config.toml");
            string text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            if (text.Contains("localeOverride"))
            {
                text = Regex.Replace(text, "localeOverride\\s*=\\s*\"[^\"]*\"", "localeOverride = \"zh-CN\"");
            }
            else if (text.Contains("[desktop]"))
            {
                text = Regex.Replace(text, "(?m)^(\\[desktop\\][^\\r\\n]*\\r?\\n)", "$1localeOverride = \"zh-CN\"\r\n");
            }
            else
            {
                text += "\r\n[desktop]\r\nlocaleOverride = \"zh-CN\"\r\n";
            }
            File.WriteAllText(path, text, new UTF8Encoding(false));
            Ok("已确保 config.toml: [desktop] localeOverride=\"zh-CN\"");
        }

        // 旧版 codex-zh-CN 包只认 .vite/build/main-*.js；新版主进程入口改为 package.json main 字段
        // 指定的 .vite/build/early-bootstrap.js —— 给补丁脚本注入"按 main 字段定位"的回退逻辑
        private static void ApplyZhCnCompatFix(string root)
        {
            try
            {
                string mjs = Path.Combine(root, "scripts", "patch-codex-zh-cn.mjs");
                if (!File.Exists(mjs)) return;
                string text = File.ReadAllText(mjs);
                if (text.Contains("CodexAppInstaller-compat")) return;
                const string oldLine = "  if (!mainAsarPath) throw new Error(\"未在 app.asar 中找到 main bundle。\");";
                const string newBlock =
                    "  // CodexAppInstaller-compat: 新版 Codex 主进程入口由 package.json main 字段指定\n" +
                    "  if (!mainAsarPath) {\n" +
                    "    try {\n" +
                    "      const pkgEntry = getAsarFileEntry(parsed.header, \"package.json\");\n" +
                    "      const pkgData = fs.readFileSync(asarPath);\n" +
                    "      const pkgOff = 8 + parsed.headerSize + Number(pkgEntry.offset);\n" +
                    "      const pkg = JSON.parse(pkgData.subarray(pkgOff, pkgOff + Number(pkgEntry.size)).toString(\"utf8\"));\n" +
                    "      if (pkg && typeof pkg.main === \"string\" && pkg.main.endsWith(\".js\")) {\n" +
                    "        mainAsarPath = pkg.main.startsWith(\"/\") ? pkg.main.slice(1) : pkg.main;\n" +
                    "        logInfo(\"按 package.json main 字段定位主进程 bundle: \" + mainAsarPath);\n" +
                    "      }\n" +
                    "    } catch {}\n" +
                    "  }\n" +
                    "  if (!mainAsarPath) throw new Error(\"未在 app.asar 中找到 main bundle。\");";
                if (!text.Contains(oldLine)) return;
                File.WriteAllText(mjs, text.Replace(oldLine, newBlock), new UTF8Encoding(false));
                Ok("已应用汉化包兼容补丁（适配新版主进程 bundle 结构）");
            }
            catch (Exception ex) { Warn("汉化包兼容补丁失败: " + Trunc(ex.Message)); }
        }

        // 旧架构（GUI=Codex.exe）：codex-zh-CN 汉化包（国内线路下载 + Node 便携版 + 补丁脚本）
        private static string LocalizeLegacy(AppxInfo appx)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                Step("6/11 获取汉化包信息（" + ZhCnRepo + "）");
                string tag = GetGitHubLatestTag(ZhCnRepo);
                if (tag == null) { tag = ZhCnPinTag; Warn("无法获取最新版本号，使用固定版本 " + tag); }
                Ok("汉化包版本: " + tag);
                string zipName = "codex-zh-CN-" + tag + ".zip";
                string zipPath = Path.Combine(CacheDir, zipName);
                if (File.Exists(zipPath) && new FileInfo(zipPath).Length > 10000)
                {
                    Ok("缓存命中: " + zipPath);
                }
                else
                {
                    bool dl = false;
                    foreach (string prefix in ZhCnSources())
                    {
                        string url = prefix + "/" + ZhCnRepo + "/releases/download/" + tag + "/" + zipName;
                        Step("尝试下载源: " + prefix);
                        if (DownloadFile(url, zipPath, "codex-zh-CN 汉化包", 10000)) { dl = true; break; }
                    }
                    if (!dl) throw new Exception("汉化包所有下载源均失败");
                }

                Step("7/11 解压并执行汉化");
                string extractDir = Path.Combine(CacheDir, "codex-zh-CN-" + tag);
                if (Directory.Exists(extractDir)) { try { Directory.Delete(extractDir, true); } catch { } }
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                string root = FindZhCnRoot(extractDir);
                if (root == null) throw new Exception("解压后未找到 scripts\\install_windows.ps1");
                ApplyZhCnCompatFix(root);
                string nodeDir = EnsureNode();
                string ps1 = Path.Combine(root, "scripts", "install_windows.ps1");
                string pargs = "-NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\" -Action install";
                if (appx != null && !string.IsNullOrEmpty(appx.InstallLocation))
                    pargs += " -CodexPath \"" + appx.InstallLocation + "\"";
                pargs += " -NoPause";
                Console.WriteLine("  运行: powershell " + pargs);
                Console.WriteLine("  （若 Codex 正在运行会先自动关闭，完成后自动重启）");
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", pargs);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                string oldPath = psi.EnvironmentVariables["PATH"];
                psi.EnvironmentVariables["PATH"] = nodeDir + (string.IsNullOrEmpty(oldPath) ? "" : ";" + oldPath);
                using (Process p = Process.Start(psi))
                {
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Console.WriteLine("  | " + e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Console.WriteLine("  | " + e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        Warn("汉化脚本退出码 " + p.ExitCode + "（不影响主体安装）");
                        Warn("可手动双击运行: " + Path.Combine(root, "install-windows.bat"));
                        return null;
                    }
                }
                Ok("汉化脚本执行成功");
                string exe = GetPatchedExe();
                if (exe == null) Warn("未找到汉化补丁副本记录（zh-cn-patched-active.txt），快捷方式仍指向原版");
                return exe;
            }
            catch (Exception ex)
            {
                Fail("汉化失败: " + Trunc(ex.Message) + "（不影响主体安装）");
                return null;
            }
        }

        private static string FindZhCnRoot(string extractDir)
        {
            string marker = Path.Combine("scripts", "install_windows.ps1");
            if (File.Exists(Path.Combine(extractDir, marker))) return extractDir;
            try
            {
                foreach (string d in Directory.GetDirectories(extractDir))
                    if (File.Exists(Path.Combine(d, marker))) return d;
            }
            catch { }
            return null;
        }

        // 汉化脚本依赖 Node.js；系统未安装时从 npmmirror（国内镜像）下载便携版
        private static string EnsureNode()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string p in path.Split(';'))
            {
                string t = p != null ? p.Trim().Trim('"') : "";
                if (t.Length > 0 && File.Exists(Path.Combine(t, "node.exe"))) return t;
            }
            string baseDir = Path.Combine(CacheDir, "node");
            string nodeDir = Path.Combine(baseDir, "node-" + NodeVersion + "-win-x64");
            if (!File.Exists(Path.Combine(nodeDir, "node.exe")))
            {
                Step("未检测到系统 Node.js，从国内镜像下载便携版");
                string zipPath = Path.Combine(CacheDir, "node-" + NodeVersion + "-win-x64.zip");
                string url = "https://registry.npmmirror.com/-/binary/node/" + NodeVersion + "/node-" + NodeVersion + "-win-x64.zip";
                if (File.Exists(zipPath) && new FileInfo(zipPath).Length < 15000000) File.Delete(zipPath);
                if (!File.Exists(zipPath))
                {
                    if (!DownloadFile(url, zipPath, "Node.js 便携版 (" + NodeVersion + ")", 15000000))
                        throw new Exception("Node.js 便携版下载失败（汉化脚本需要 Node.js 运行时）");
                }
                if (Directory.Exists(nodeDir)) { try { Directory.Delete(nodeDir, true); } catch { } }
                Directory.CreateDirectory(baseDir);
                ZipFile.ExtractToDirectory(zipPath, baseDir);
                if (!File.Exists(Path.Combine(nodeDir, "node.exe"))) throw new Exception("Node 便携包解压异常");
            }
            Ok("使用 Node.js: " + nodeDir);
            return nodeDir;
        }

        // 读取 codex-zh-CN 写入的补丁副本记录（首行 = 补丁根目录，exe 在 app\ 下）
        private static string GetPatchedExe()
        {
            try
            {
                string active = CodexHomePath("zh-cn-patched-active.txt");
                if (!File.Exists(active)) return null;
                foreach (string line in File.ReadAllLines(active))
                {
                    string root = line != null ? line.Trim() : "";
                    if (root.Length == 0) continue;
                    foreach (string n in new string[] { "Codex.exe", "codex.exe" })
                    {
                        string exe = Path.Combine(root, "app", n);
                        if (File.Exists(exe)) return exe;
                    }
                    return null;
                }
            }
            catch { }
            return null;
        }

        // ================= 完成提示 =================
        private static void PrintCompletion(AppxInfo appx, string key, string patchedExe)
        {
            Console.WriteLine();
            Console.WriteLine("===============================================");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" 安装与配置全部完成！");
            Console.ResetColor();
            if (appx != null) Console.WriteLine(" [完成] Codex Desktop 已安装: " + appx.Name + " " + appx.Version);
            if (!string.IsNullOrEmpty(key))
                Console.WriteLine(" [完成] Xode AI平台（" + GetSite() + "）API Key「" + Sub2apiKeyName + "」: " + MaskKey(key));
            Console.WriteLine(" [完成] 配置: 搜索=live、Full Access、记忆=开、启动更新检查=关");
            if (XodeBrandingOk)
                Console.WriteLine(" [完成] Xode AI 品牌化: 桌面/启动器图标已更换");
            if (PluginMarketplaceOk)
                Console.WriteLine(" [完成] 插件市场已内置（全部国内直连安装）");
            Console.WriteLine(" [完成] 中文界面已就绪");
            Console.WriteLine("-----------------------------------------------");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" 使用说明:");
            Console.WriteLine("  1. 双击桌面「Codex」图标启动，即可获得完整中文界面");
            Console.WriteLine("  2. 插件页可浏览并安装内置插件市场的插件（全部国内直连）");
            Console.WriteLine("  3. 如界面仍为英文: 打开 Settings → General → Language → 中文（中国）");
            Console.WriteLine("  4. 设置页「完全访问权限」开关请手动打开一次（设置 → 常规 → 权限；对话内权限选择器已默认完全访问）");
            Console.WriteLine("  5. 修改配置后需完全退出 Codex（托盘右键退出）再重新打开");
            Console.WriteLine("  6. API Key 可在 " + GetSite() + " 后台「API 密钥」页管理");
            Console.WriteLine("  7. 启动更新检查已关闭；若仍弹「新版本可用」提示，忽略即可（官方已知问题）");
            Console.ResetColor();
            Console.WriteLine("===============================================");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Codex App（ChatGPT Desktop）一键安装程序（" + BuildInfo.Time + "版本、纯 .NET 实现）");
            Console.WriteLine();
            Console.WriteLine("用法: CodexAppInstaller.exe [参数...]");
            Console.WriteLine();
            Console.WriteLine("参数:");
            Console.WriteLine("  -Site <url>        Xode AI平台地址（默认 " + Sub2apiSiteDefault + "）");
            Console.WriteLine("  -Email <邮箱>      Xode AI平台 账号（免交互，登录站点创建/复用 Key）");
            Console.WriteLine("  -Password <密码>   Xode AI平台 密码（免交互）");
            Console.WriteLine("  -SkipChecksum      跳过 SHA256 校验（不推荐）");
            Console.WriteLine("  -SkipShortcut      不创建桌面快捷方式");
            Console.WriteLine("  -SkipApi           跳过 Xode AI平台 配置");
            Console.WriteLine("  -SkipZhCn          跳过汉化");
            Console.WriteLine("  -TestRelay         仅测试 Xode AI平台 登录/建Key/写配置（不安装不汉化，无需管理员）");
            Console.WriteLine("  -NoElevate         跳过 UAC 提权（高级：机器级注册与部分汉化操作可能失败）");
            Console.WriteLine("  -LogFile <path>    输出同时写入日志文件（提权运行时留档排障）");
            Console.WriteLine("  -Logo <path>       Xode AI logo（ico/png，用于桌面与启动器图标品牌化；默认取安装器同目录 xode-logo.ico/png）");
            Console.WriteLine("  -ApiBaseUrl <url>  手动模式的聚合端点（默认 " + DefaultApiBaseUrl + "，需 OpenAI Responses 兼容）");
            Console.WriteLine("  -ApiKey <key>      手动模式 API Key（提供后跳过 Xode AI平台 登录，格式 sk-xxxxxx）");
            Console.WriteLine("  -ApiModel <model>  手动模式模型（默认 " + DefaultModel + "）");
        }
    }
}
