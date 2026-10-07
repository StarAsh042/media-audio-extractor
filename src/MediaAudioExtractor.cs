// ============================================================================
//  视频音频提取器  v1.0.0  (Media Audio Extractor)
//  Copyright (c) 2026 StarAsh042  ·  MIT License
//
//  界面流程：
//    第 1 步  输入网址（或选择 / 拖入 / 粘贴本地音频·视频文件）→ 选择音质 → 获取【完整音频】
//    第 2 步  波形播放器：播放/暂停/停止/试听选区，波形上拖动左右操作杆选取起止
//    第 3 步  裁剪选区并保存为 mp3 / m4a / wav
//  界面明暗跟随系统主题（浅色 / 深色），系统切换时实时跟随。
//
//  命令行（便于自动化与批量）：
//    --cli --url <网址> --out <目录> [--start 0] [--dur 15] [--format mp3] [--no-section]
//    --cli --full --url <网址> --out <目录> [--quality best|std|low]
//    --cli --cut <本地文件> --out <目录> [--start 0] [--end 15] [--format mp3] [--name 名称]
//    --cli --preview <本地文件> --wav <输出.wav> [--rate 44100]
//    --cli --peaks <wav> [--buckets 1000]
//
//  依赖：内置 yt-dlp.exe 与 ffmpeg.exe（作为资源嵌入 exe，首次运行解压）
//  播放：winmm MCI（waveaudio），无需任何第三方音频库
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("视频音频提取器")]
[assembly: AssemblyDescription("在线视频音频提取器：下载完整音频、波形选区试听、裁剪导出（内置 yt-dlp + ffmpeg）")]
[assembly: AssemblyCompany("StarAsh042")]
[assembly: AssemblyProduct("MediaAudioExtractor")]
[assembly: AssemblyCopyright("Copyright (c) 2026 StarAsh042")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace MediaAudioExtractor
{
    internal static class Program
    {
        /// <summary>程序版本（与程序集版本、README、CHANGELOG 保持一致）。</summary>
        public const string AppVersion = "1.0.0";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                if (args[0] == "--dumpui") { return UiProbe.Run(args); }
                if (args[0] == "--uiauto") { return UiProbe.RunAuto(args); }
                if (args[0] == "--themetest") { return UiProbe.RunThemeTest(args); }
                return Cli.Run(args);
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }

    // ------------------------------------------------------------------
    //  时间格式化 / 小工具
    // ------------------------------------------------------------------
    internal static class Fmt
    {
        public static string Num(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 0.0005)
            {
                return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            }
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string Clock(double seconds)
        {
            if (seconds < 0) { seconds = 0; }
            int total = (int)seconds;
            int m = total / 60;
            int s = total % 60;
            return m.ToString("00") + ":" + s.ToString("00") + "." + ((int)((seconds - total) * 10)).ToString("0");
        }

        public static string Len(double seconds)
        {
            int total = (int)Math.Round(seconds);
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }
    }

    // ------------------------------------------------------------------
    //  下载 / 转码 引擎
    // ------------------------------------------------------------------
    internal sealed class ExtractionRequest
    {
        public string Url;
        public string OutDir;
        public string Format = "mp3";
        public double Start;
        public double Duration;      // <=0 表示整段
        public bool NoSection;
    }

    internal sealed class ExtractionResult
    {
        public string FilePath;
        public double Duration;
    }

    internal sealed class AudioSession
    {
        public string SourcePath;    // 原始完整音频（工作目录内，ASCII 路径）
        public string PreviewWav;    // 波形/区间读取用的 WAV
        public string PlaybackPath;  // 试听播放的文件：本地文件=原文件本身；下载=试听 WAV
        public string Title;
        public double Duration;      // 秒
        public string FormatNote;    // 例如 "m4a / 110 kbps"
        public float[] Peaks;        // 解码时顺带算好的整段包络（无需再读一遍 WAV）
    }

    // ------------------------------------------------------------------
    //  内置组件（yt-dlp / ffmpeg）：首次使用时解压到本地缓存目录
    // ------------------------------------------------------------------
    internal static class Tools
    {
        private static readonly object Sync = new object();
        private static string _dir;

        public static string Dir { get { Ensure(); return _dir; } }
        public static string YtDlpPath { get { return Path.Combine(Dir, "yt-dlp.exe"); } }
        public static string FfmpegPath { get { return Path.Combine(Dir, "ffmpeg.exe"); } }

        public static void Ensure()
        {
            if (_dir != null) { return; }
            lock (Sync)
            {
                if (_dir != null) { return; }
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MediaAudioExtractor", "bin");
                Directory.CreateDirectory(dir);
                ExtractEmbedded("yt-dlp.exe", Path.Combine(dir, "yt-dlp.exe"));
                ExtractEmbedded("ffmpeg.exe", Path.Combine(dir, "ffmpeg.exe"));
                _dir = dir;
            }
        }

        /// <summary>
        /// 内置组件的 SHA-256，与 tools/fetch-tools.ps1 里固定的版本一致。
        /// 用于确认「解压出来的就是内嵌的那一份」。
        /// </summary>
        private const string YtDlpSha256 = "66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A";
        private const string FfmpegSha256 = "2CE797A0F88D7F067180338FB227F7B1928EA727BD9A4D7A1D022F7C52AF71A3";

        private static void ExtractEmbedded(string resourceName, string dest)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null)
                {
                    throw new InvalidOperationException("程序缺少内置组件：" + resourceName);
                }
                FileInfo fi = new FileInfo(dest);
                if (fi.Exists && fi.Length == s.Length) { return; }

                string tmp = dest + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[1 << 20];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) { fs.Write(buf, 0, n); }
                }

                // 校验刚写出来的字节确实是内嵌的那一份（防解压损坏 / 磁盘出错）。
                // 只在「本次真的解压了」时才计算哈希，所以不会拖慢日常启动。
                string expect = resourceName.StartsWith("yt-dlp", StringComparison.OrdinalIgnoreCase)
                              ? YtDlpSha256 : FfmpegSha256;
                string actual = Sha256File(tmp);
                if (!string.Equals(actual, expect, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    throw new InvalidOperationException(
                        "内置组件 " + resourceName + " 校验失败（期望 " + expect + "，实际 " + actual + "）。" +
                        "请重新下载本程序。");
                }

                if (File.Exists(dest)) { File.Delete(dest); }
                File.Move(tmp, dest);
            }
        }

        /// <summary>计算文件的 SHA-256（大写十六进制）。</summary>
        public static string Sha256File(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) { sb.Append(hash[i].ToString("X2")); }
                return sb.ToString();
            }
        }
    }
    internal sealed class Extractor
    {
        public event Action<string> Log;
        public event Action<string> Status;
        public event Action<int> Progress;      // -1 = 不确定进度

        private readonly object _lock = new object();
        private Process _proc;
        private volatile bool _cancel;

        /// <summary>整段波形的包络桶数（约一桶一像素）。</summary>
        public const int PeakBuckets = 1400;

        public void Cancel()
        {
            _cancel = true;
            Process p;
            lock (_lock) { p = _proc; }
            if (p != null)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                }
                catch (Exception) { }
            }
        }

        // ============ 一、下载【完整音频】并生成试听 WAV ============
        public AudioSession DownloadWhole(string url, string outDir, string quality, string workDir,
                                          Action<double, float[], double> onProgress = null)
        {
            Tools.Ensure();
            _cancel = false;
            // 网址会被拼进 yt-dlp 命令行，先挡住能突破引号边界的输入
            if (!IsSafeUrl(url))
            {
                throw new InvalidOperationException(
                    "网址无效：只接受以 http:// 或 https:// 开头、且不含引号或空白的网址。");
            }
            ResetWorkDir(workDir);

            string template = Path.Combine(workDir, "media.%(ext)s");
            string titleFile = Path.Combine(workDir, "title.txt");
            string args = "--no-warnings --newline --no-playlist --no-mtime" +
                          " --print-to-file " + Quote("%(title)s") + " " + Quote(titleFile) +
                          " --retries 10 --fragment-retries 10 --socket-timeout 20" +
                          " --ffmpeg-location " + Quote(Tools.Dir) +
                          " -f " + QualitySelector(quality) +
                          " -o " + Quote(template) + " " + Quote(url);

            EmitStatus("正在下载完整音频 …");
            int code = RunYtDlp(args);
            ThrowIfCancelled();
            if (code != 0)
            {
                throw new InvalidOperationException("下载失败（yt-dlp 退出码 " + code +
                    "）。请检查网址是否正确，或该内容是否需要登录。");
            }

            string src = PickMediaFile(workDir);
            if (src == null)
            {
                EmitLog("工作目录内容：" + ListDir(workDir));
                throw new InvalidOperationException("没有找到下载到的音频文件。");
            }
            string title = ReadFirstLine(titleFile);
            if (string.IsNullOrEmpty(title)) { title = "audio"; }
            EmitLog("已下载音频：" + Path.GetFileName(src) + "（" + (new FileInfo(src).Length / 1024) + " KB）");
            EmitLog("标题：" + title);

            double dur = ProbeDuration(src);
            EmitLog("完整时长：" + Fmt.Len(dur) + "（" + dur.ToString("0.0") + " 秒）");

            // 波形缓存 WAV：44.1k 单声道；超长音频降到 22.05k 以控制体积。
            // 解码与包络计算在同一次 ffmpeg 调用里完成，界面可边解码边画波形。
            if (dur <= 0.05)
            {
                throw new InvalidOperationException("音频时长为 0，无法试听与裁剪。");
            }
            int rate = dur > 900 ? 22050 : 44100;
            string wav = NewPreviewWavPath(workDir);
            EmitStatus("正在解码并绘制波形（" + rate / 1000 + " kHz 单声道）…");
            float[] peaks = DecodeWithPeaks(src, wav, rate, PeakBuckets, dur, onProgress);
            ThrowIfCancelled();

            // 完整音频保存到用户目录（中文/空格路径由 .NET 处理，不交给外部程序）
            string keepName = SanitizeName(title) + Path.GetExtension(src);
            string keepPath = UniquePath(Path.Combine(outDir, keepName));
            File.Copy(src, keepPath, false);
            EmitLog("完整音频已保存：" + keepPath);

            AudioSession s = new AudioSession();
            s.SourcePath = src;
            s.PreviewWav = wav;
            s.PlaybackPath = wav;       // 下载来的格式不定（可能是 webm/opus），仍用 WAV 试听最稳
            s.Title = title;
            s.Duration = dur;
            s.FormatNote = DetectFormatNote(src);
            s.Peaks = peaks;
            return s;
        }

        private static string QualitySelector(string quality)
        {
            if (quality == "low") { return "\"bestaudio[abr<=70]/bestaudio/best\""; }
            if (quality == "std") { return "\"bestaudio[abr<=128]/bestaudio/best\""; }
            return "bestaudio/best";
        }

        /// <summary>
        /// 判断输入是否为「已存在的本地文件」，是则返回完整路径，否则返回 null。
        /// 容忍资源管理器「复制为路径」带来的首尾引号与空白。
        /// </summary>
        /// <summary>
        /// 是否为网络（UNC）路径。
        /// 必须在任何 File.Exists / ffmpeg 之前判断 —— 那些调用本身就会向该主机
        /// 发起 SMB 连接，可能把本机 NTLM 凭据交给对方。
        /// </summary>
        public static bool IsUncPath(string s)
        {
            if (string.IsNullOrEmpty(s)) { return false; }
            string t = s.Trim();
            return t.StartsWith("\\\\") || t.StartsWith("//");
        }

        public static string ResolveLocalFile(string input)
        {
            if (string.IsNullOrEmpty(input)) { return null; }
            string s = input.Trim().Trim('"').Trim();
            if (s.Length == 0) { return null; }
            // UNC 路径直接按「不是本地文件」处理，绝不能走到 File.Exists
            if (IsUncPath(s)) { return null; }
            try
            {
                if (File.Exists(s)) { return Path.GetFullPath(s); }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// 载入本地音频/视频文件：跳过 yt-dlp，也**不复制原文件** ——
        /// 直接以原路径交给 ffmpeg / 播放器，省掉一份等体积的磁盘副本。
        /// 试听直接播放原文件（WpfPlayer 走 Media Foundation），
        /// 因此这里生成的 WAV 只作为「按区间快速读取波形」的缓存，不再是试听文件。
        /// </summary>
        public AudioSession LoadLocal(string path, string workDir,
                                      Action<double, float[], double> onProgress = null)
        {
            Tools.Ensure();
            _cancel = false;

            // 网络（UNC）路径会在载入时向该主机发起 SMB 连接，可能把本机 NTLM 凭据交给对方，
            // 因此直接拒绝，要求先复制到本地磁盘。
            if (IsUncPath(path))
            {
                throw new InvalidOperationException(
                    "不支持网络路径（\\\\ 开头）。载入它会向该地址发起 SMB 连接并可能泄露本机凭据；" +
                    "请先把文件复制到本地磁盘再载入。");
            }

            ResetWorkDir(workDir);

            string src = path;

            EmitStatus("正在载入本地文件 …");
            EmitProgress(-1);

            string title = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(title)) { title = "audio"; }
            EmitLog("本地文件：" + path + "（" + (new FileInfo(path).Length / 1024) + " KB）");
            EmitLog("标题：" + title);

            double dur = ProbeDuration(src);
            EmitLog("完整时长：" + Fmt.Len(dur) + "（" + dur.ToString("0.0") + " 秒）");
            if (dur <= 0.05)
            {
                throw new InvalidOperationException("音频时长为 0，或无法识别该文件格式。");
            }

            // 波形缓存 WAV：与下载路径同规则，44.1k 单声道；超长音频降到 22.05k 以控制体积。
            // 解码与包络计算在同一次 ffmpeg 调用里完成，界面可边解码边画波形。
            int rate = dur > 900 ? 22050 : 44100;
            string wav = NewPreviewWavPath(workDir);
            EmitStatus("正在解码并绘制波形（" + rate / 1000 + " kHz 单声道）…");
            float[] peaks = DecodeWithPeaks(src, wav, rate, PeakBuckets, dur, onProgress);
            ThrowIfCancelled();

            AudioSession s = new AudioSession();
            s.SourcePath = src;
            s.PreviewWav = wav;
            s.PlaybackPath = path;      // 试听直接用原文件
            s.Title = title;
            s.Duration = dur;
            s.FormatNote = DetectFormatNote(path);
            s.Peaks = peaks;
            return s;
        }

        /// <summary>
        /// 清空并重建工作目录。若目录内仍有文件被占用（例如上一份试听 WAV 尚被播放器持有），
        /// 删除会失败 —— 此时只记录日志并继续：试听文件名是唯一的，不会互相覆盖。
        /// </summary>
        private void ResetWorkDir(string workDir)
        {
            if (Directory.Exists(workDir))
            {
                try { Directory.Delete(workDir, true); }
                catch (Exception)
                {
                    EmitLog("⚠ 工作目录未能完全清空（有文件仍被占用）；本次改用新的试听文件名，不影响载入。");
                }
            }
            Directory.CreateDirectory(workDir);
        }

        /// <summary>
        /// 试听 WAV 使用唯一文件名：万一上一份仍被外部程序占用，
        /// 也不会因为无法覆盖而让新的载入失败。
        /// </summary>
        private static string NewPreviewWavPath(string workDir)
        {
            return Path.Combine(workDir, "preview-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".wav");
        }

        /// <summary>
        /// ffmpeg 生成试听文件失败时，依据 stderr 给出更贴近真实原因的提示
        /// （例如「Permission denied」其实是被占用，而非文件格式有问题）。
        /// </summary>
        private static string PreviewFailHint(string note)
        {
            if (!string.IsNullOrEmpty(note) &&
                note.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "试听文件被其他程序占用，无法写入，请关闭占用它的程序后重试。";
            }
            return "请确认它是音频或视频文件。";
        }

        private static string DetectFormatNote(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext.Length > 1 ? ext.Substring(1) : ext;
        }

        // ============ 二、从本地文件裁剪出片段 ============
        public string CutLocalFile(string src, double start, double dur, string format,
                                   string outDir, string baseName, string workDir)
        {
            Tools.Ensure();
            _cancel = false;
            // 以前这里把 0 或负数悄悄钳成 0.001 秒，结果「空选区」也能导出一个 0.05 秒的文件，
            // 与 GUI（直接提示「选区太短」）行为不一致。现在两端统一为明确报错。
            if (dur <= 0.05)
            {
                throw new InvalidOperationException(
                    "选区为空或过短：结束时间必须大于开始时间，且不短于 0.05 秒。");
            }
            string ext = NormalizeFormat(format);
            string tmpOut = Path.Combine(workDir, "export_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "." + ext);
            string note;
            EmitStatus("正在裁剪并转码 …");
            EmitProgress(-1);
            int code = RunFfmpeg(BuildFfmpegArgs(src, tmpOut, ext, start, dur), out note);
            ThrowIfCancelled();
            if (code != 0)
            {
                throw new InvalidOperationException("裁剪失败（ffmpeg 退出码 " + code + "）。" +
                    (note.Length > 0 ? " " + note : ""));
            }
            string name = SanitizeName(baseName);
            if (name.Length == 0) { name = "audio"; }
            string finalPath = UniquePath(Path.Combine(outDir, name + "." + ext));
            File.Copy(tmpOut, finalPath, false);
            try { File.Delete(tmpOut); } catch (Exception) { }
            EmitLog("已保存：" + finalPath);
            return finalPath;
        }

        // ============ 三、波形峰值 ============
        public float[] BuildPeaks(string wavPath, int buckets)
        {
            double seconds;
            return ReadPeaksFromWav(wavPath, buckets, out seconds);
        }

        /// <summary>
        /// 一次 ffmpeg 调用同时做两件事：
        ///   · 把 16bit 单声道 PCM 写到 stdout —— 供「边解码边算包络」，实现渐进绘制
        ///   · 写出 WAV 文件 —— 供后续放大时按区间快速读取波形
        /// 因此只解码一遍，不会比原来多花一倍时间。
        /// onProgress(duration, peaks, fraction)：开始时给 (dur, null, 0)，
        /// 之后定期给已解码部分的包络副本与进度，供界面增量刷新。
        /// </summary>
        public float[] DecodeWithPeaks(string src, string wavPath, int rate, int buckets,
                                       double duration, Action<double, float[], double> onProgress)
        {
            _cancel = false;
            if (buckets < 16) { buckets = 16; }
            float[] peaks = new float[buckets];
            long totalSamples = (long)Math.Max(1.0, duration * rate);

            if (onProgress != null) { onProgress(duration, null, 0); }

            string args = "-hide_banner -loglevel error -nostdin -y -i " + Quote(src) +
                          " -vn -ac 1 -ar " + rate + " -f s16le pipe:1" +
                          " -vn -ac 1 -ar " + rate + " -c:a pcm_s16le " + Quote(wavPath);

            ProcessStartInfo psi = new ProcessStartInfo(Tools.FfmpegPath, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                lock (_lock) { _proc = p; }
                try
                {
                    p.Start();

                    // stderr 必须持续读走，否则管道写满会把 ffmpeg 卡死
                    StringBuilder errBuf = new StringBuilder();
                    Thread errReader = new Thread(new ThreadStart(delegate
                    {
                        try
                        {
                            string line;
                            while ((line = p.StandardError.ReadLine()) != null)
                            {
                                if (errBuf.Length < 4000) { errBuf.AppendLine(line); }
                            }
                        }
                        catch (Exception) { }
                    }));
                    errReader.IsBackground = true;
                    errReader.Start();

                    Stream input = p.StandardOutput.BaseStream;
                    byte[] buf = new byte[1 << 16];
                    long done = 0;
                    long lastPost = -1000;
                    Stopwatch sw = Stopwatch.StartNew();

                    while (true)
                    {
                        if (_cancel) { break; }
                        int got;
                        try { got = input.Read(buf, 0, buf.Length); }
                        catch (Exception) { break; }
                        if (got <= 0) { break; }

                        int frames = got / 2;
                        for (int k = 0; k < frames; k++)
                        {
                            int v = (short)(buf[k * 2] | (buf[k * 2 + 1] << 8));
                            int a = v < 0 ? -v : v;
                            long idx = done + k;
                            int b = (int)(idx * buckets / totalSamples);
                            if (b < 0) { b = 0; }
                            if (b >= buckets) { b = buckets - 1; }
                            float f = a / 32768f;
                            if (f > peaks[b]) { peaks[b] = f; }
                        }
                        done += frames;

                        if (onProgress != null && sw.ElapsedMilliseconds - lastPost >= 150)
                        {
                            lastPost = sw.ElapsedMilliseconds;
                            double frac = done / (double)totalSamples;
                            if (frac > 1) { frac = 1; }
                            onProgress(duration, (float[])peaks.Clone(), frac);
                        }
                    }

                    int code = 0;
                    try { p.WaitForExit(10000); code = p.ExitCode; } catch (Exception) { }
                    // 进程虽已退出，但读 stderr 的线程可能还在收尾。
                    // StringBuilder 不是线程安全的，边 Append 边读会拿到截断/为空的报错文案，
                    // 所以先等它一下再读。
                    try { errReader.Join(500); } catch (Exception) { }
                    if (_cancel) { throw new OperationCanceledException("用户已取消。"); }
                    if (code != 0)
                    {
                        string note = errBuf.ToString().Trim();
                        if (note.Length > 0) { EmitLog("ffmpeg: " + note); }
                        throw new InvalidOperationException("解码失败（ffmpeg 退出码 " + code + "）。" +
                            PreviewFailHint(note));
                    }
                    return peaks;
                }
                finally
                {
                    lock (_lock) { _proc = null; }
                }
            }
        }

        public static float[] ReadPeaksFromWav(string path, int buckets, out double seconds)
        {
            return ReadPeaksRange(path, 0, -1, buckets, out seconds);
        }

        private static bool OpenWav(string path, out FileStream fs, out int rate, out int channels,
                                    out long totalSamples, out long dataOffset)
        {
            fs = null; rate = 44100; channels = 1; totalSamples = 0; dataOffset = -1;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                BinaryReader br = new BinaryReader(fs);
                if (new string(br.ReadChars(4)) != "RIFF") { return false; }
                br.ReadUInt32();
                if (new string(br.ReadChars(4)) != "WAVE") { return false; }
                int bits = 16;
                long dataLen = 0;
                while (fs.Position + 8 <= fs.Length)
                {
                    string id = new string(br.ReadChars(4));
                    uint size = br.ReadUInt32();
                    if (id == "fmt ")
                    {
                        int af = br.ReadUInt16();
                        channels = br.ReadUInt16();
                        rate = br.ReadInt32();
                        br.ReadInt32();
                        br.ReadUInt16();
                        bits = br.ReadUInt16();
                        long skip = (long)size - 16;
                        if (skip > 0) { fs.Seek(skip, SeekOrigin.Current); }
                        if (af != 1 || bits != 16) { return false; }
                    }
                    else if (id == "data")
                    {
                        dataOffset = fs.Position;
                        dataLen = size;
                        break;
                    }
                    else
                    {
                        fs.Seek((long)size + (size % 2), SeekOrigin.Current);
                    }
                }
                if (dataOffset < 0 || dataLen <= 0) { return false; }
                totalSamples = dataLen / (2L * channels);
                return true;
            }
            catch (Exception)
            {
                if (fs != null) { try { fs.Dispose(); } catch (Exception) { } fs = null; }
                return false;
            }
        }

        /// <summary>读取 [t0,t1] 区间的波形包络（t1&lt;=0 表示到结尾）。</summary>
        public static float[] ReadPeaksRange(string path, double t0, double t1, int buckets,
                                             out double totalSeconds)
        {
            totalSeconds = 0;
            if (buckets < 16) { buckets = 16; }
            float[] peaks = new float[buckets];
            FileStream fs = null;
            int rate, channels;
            long totalSamples, dataOffset;
            if (!OpenWav(path, out fs, out rate, out channels, out totalSamples, out dataOffset))
            {
                if (fs != null) { try { fs.Dispose(); } catch (Exception) { } }
                return peaks;
            }
            try
            {
                totalSeconds = totalSamples / (double)rate;
                if (t0 < 0) { t0 = 0; }
                if (t1 <= 0 || t1 > totalSeconds) { t1 = totalSeconds; }
                long s0 = (long)(t0 * rate);
                long s1 = (long)(t1 * rate);
                if (s0 < 0) { s0 = 0; }
                if (s1 > totalSamples) { s1 = totalSamples; }
                long count = s1 - s0;
                if (count <= 0) { return peaks; }

                fs.Seek(dataOffset + s0 * 2L * channels, SeekOrigin.Begin);
                int bufBytes = 1 << 16;
                byte[] buf = new byte[bufBytes];
                long done = 0;
                float max = 0f;
                while (done < count)
                {
                    long leftSamples = count - done;
                    int want = (int)Math.Min(bufBytes, leftSamples * 2L * channels);
                    want -= want % (2 * channels);
                    if (want <= 0) { break; }
                    int got = fs.Read(buf, 0, want);
                    if (got <= 0) { break; }
                    int frames = got / (2 * channels);
                    for (int i = 0; i < frames; i++)
                    {
                        int a = 0;
                        for (int c = 0; c < channels; c++)
                        {
                            int o = (i * channels + c) * 2;
                            short v = (short)(buf[o] | (buf[o + 1] << 8));
                            int av = v < 0 ? -v : v;
                            if (av > a) { a = av; }
                        }
                        int b = (int)(((done + i) * (long)buckets) / count);
                        if (b >= buckets) { b = buckets - 1; }
                        if (a > peaks[b]) { peaks[b] = a; }
                        if (a > max) { max = a; }
                    }
                    done += frames;
                }
                if (max > 0)
                {
                    for (int i = 0; i < buckets; i++) { peaks[i] = peaks[i] / max; }
                }
            }
            catch (Exception) { }
            finally
            {
                try { fs.Dispose(); } catch (Exception) { }
            }
            return peaks;
        }

        // ============ 兼容旧版：一次性下载片段 ============
        public ExtractionResult Run(ExtractionRequest req)
        {
            Tools.Ensure();
            _cancel = false;
            if (!IsSafeUrl(req.Url))
            {
                throw new InvalidOperationException(
                    "网址无效：只接受以 http:// 或 https:// 开头、且不含引号或空白的网址。");
            }
            string tempDir = Path.Combine(Path.GetTempPath(), "MediaAudioExtractor", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                bool wantSection = req.Duration > 0 && !req.NoSection;
                bool cutLater = req.Duration > 0 && req.NoSection;
                string template = Path.Combine(tempDir, "media.%(ext)s");
                string titleFile = Path.Combine(tempDir, "title.txt");
                string common =
                    "--no-warnings --newline --no-playlist --no-mtime" +
                    " --print-to-file " + Quote("%(title)s") + " " + Quote(titleFile) +
                    " --retries 10 --fragment-retries 10 --socket-timeout 20" +
                    " --ffmpeg-location " + Quote(Tools.Dir) +
                    " -f bestaudio/best -o " + Quote(template);

                EmitLog("临时目录：" + tempDir);
                int code = -1;
                if (wantSection)
                {
                    EmitStatus("正在下载音频片段 " + Fmt.Num(req.Start) + "s ~ " +
                               Fmt.Num(req.Start + req.Duration) + "s …");
                    string section = "*" + Fmt.Num(req.Start) + "-" + Fmt.Num(req.Start + req.Duration);
                    code = RunYtDlp(common + " --download-sections " + Quote(section) + " " + Quote(req.Url));
                }
                if (!wantSection || code != 0)
                {
                    if (wantSection && code != 0)
                    {
                        EmitLog("片段模式未成功（退出码 " + code + "），改为下载完整音频后再裁剪 …");
                        cutLater = true;
                        DeleteFilesIn(tempDir);
                    }
                    EmitStatus("正在下载完整音频 …");
                    code = RunYtDlp(common + " " + Quote(req.Url));
                }
                ThrowIfCancelled();
                if (code != 0)
                {
                    throw new InvalidOperationException("下载失败（yt-dlp 退出码 " + code +
                        "）。请检查网址是否正确，或该内容是否需要登录。");
                }

                string src = PickMediaFile(tempDir);
                if (src == null)
                {
                    EmitLog("临时目录内容：" + ListDir(tempDir));
                    throw new InvalidOperationException("没有找到下载到的音频文件。");
                }
                string title = ReadFirstLine(titleFile);
                if (string.IsNullOrEmpty(title)) { title = Path.GetFileNameWithoutExtension(src); }
                string baseName = SanitizeName(title);
                if (baseName.Length == 0) { baseName = "audio"; }

                string ext = NormalizeFormat(req.Format);
                string finalPath = UniquePath(Path.Combine(req.OutDir,
                                    baseName + BuildSuffix(req.Start, req.Duration) + "." + ext));
                string tmpOut = Path.Combine(tempDir, "output." + ext);

                EmitStatus("正在生成 " + ext.ToUpperInvariant() + " 文件 …");
                EmitProgress(-1);
                double? ss = cutLater ? (double?)req.Start : null;
                double? t = cutLater ? (double?)req.Duration : null;
                string note;
                int fc = RunFfmpeg(BuildFfmpegArgs(src, tmpOut, ext, ss, t), out note);
                ThrowIfCancelled();
                if (fc != 0) { throw new InvalidOperationException("音频转码失败（ffmpeg 退出码 " + fc + "）。"); }

                File.Copy(tmpOut, finalPath, false);
                double dur = ProbeDuration(finalPath);
                EmitProgress(100);
                EmitLog("已保存：" + finalPath);
                return new ExtractionResult { FilePath = finalPath, Duration = dur };
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch (Exception) { }
            }
        }

        // ---------------- 内部实现 ----------------

        private static void DeleteFilesIn(string dir)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch (Exception) { } }
            }
            catch (Exception) { }
        }

        private static string PickMediaFile(string dir)
        {
            string[] exts = { ".m4a", ".mp4", ".aac", ".webm", ".opus", ".mp3", ".ogg", ".mka", ".wav", ".flac" };
            string best = null;
            long bestLen = -1;
            string[] files = Directory.GetFiles(dir);
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (string f in files)
                {
                    // 跳过本程序自己的波形缓存（preview-<时间戳>.wav）：
                    // 工作目录清理失败时它会残留，否则可能被当成"刚下载到的音频"
                    if (Path.GetFileName(f).StartsWith("preview-", StringComparison.OrdinalIgnoreCase)) { continue; }
                    string e = Path.GetExtension(f).ToLowerInvariant();
                    if (pass == 0)
                    {
                        bool ok = false;
                        for (int i = 0; i < exts.Length; i++) { if (exts[i] == e) { ok = true; break; } }
                        if (!ok) { continue; }
                    }
                    else if (e == ".part" || e == ".ytdl" || e == ".txt" || e == ".json" ||
                             e == ".description" || e == ".temp" || e == "")
                    {
                        continue;
                    }
                    long len = new FileInfo(f).Length;
                    if (len > bestLen) { bestLen = len; best = f; }                }
                if (best != null) { return best; }
            }
            return best;
        }

        private static string ListDir(string dir)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (string f in Directory.GetFiles(dir))
                {
                    sb.Append(Path.GetFileName(f)).Append(" (").Append(new FileInfo(f).Length).Append("B) ");
                }
                return sb.Length == 0 ? "（空）" : sb.ToString();
            }
            catch (Exception e) { return "读取失败：" + e.Message; }
        }

        private static string ReadFirstLine(string path)
        {
            try
            {
                if (!File.Exists(path)) { return null; }
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i].Trim();
                    if (s.Length > 0) { return s; }
                }
            }
            catch (Exception) { }
            return null;
        }

        private static string NormalizeFormat(string f)
        {
            if (f == null) { return "mp3"; }
            string s = f.Trim().ToLowerInvariant();
            if (s.StartsWith("m4a") || s.StartsWith("aac")) { return "m4a"; }
            if (s.StartsWith("wav")) { return "wav"; }
            return "mp3";
        }

        public static string SanitizeName(string name)
        {
            if (name == null) { return ""; }
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = true;
                for (int j = 0; j < bad.Length; j++) { if (bad[j] == c) { ok = false; break; } }
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString().Trim().TrimEnd('.');
            if (s.Length > 80) { s = s.Substring(0, 80).TrimEnd(' ', '.'); }
            // Windows 保留设备名（CON / NUL / COM1 …）即使带扩展名也不能作为文件名，
            // 写入会落到设备上或直接失败，这里统一加前缀避开。
            if (IsReservedName(s)) { s = "_" + s; }
            return s;
        }

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>判断是否为 Windows 保留设备名（忽略扩展名与尾部空格/点）。</summary>
        public static bool IsReservedName(string s)
        {
            if (string.IsNullOrEmpty(s)) { return false; }
            string b = s;
            int dot = b.IndexOf('.');
            if (dot >= 0) { b = b.Substring(0, dot); }
            b = b.TrimEnd(' ', '.');
            for (int i = 0; i < ReservedNames.Length; i++)
            {
                if (string.Equals(b, ReservedNames[i], StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// 按 Windows 命令行规则（CommandLineToArgvW）给单个参数加引号并转义。
        ///
        /// .NET Framework 没有 ProcessStartInfo.ArgumentList，只能手工拼命令行，
        /// 所以必须做对：**反斜杠只有在引号前才需要翻倍**，结尾的连续反斜杠也要翻倍。
        /// 只把参数塞进一对双引号是不够的 —— 内容里的 `"` 会直接突破引号边界，
        /// 让调用方多解析出一个参数（例如给 yt-dlp 注入 `--exec`）。
        /// </summary>
        public static string Quote(string arg)
        {
            if (arg == null) { arg = ""; }
            StringBuilder sb = new StringBuilder(arg.Length + 8);
            sb.Append('"');
            int backslashes = 0;
            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);   // 引号前的反斜杠翻倍，并转义引号本身
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }
                if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
                sb.Append(c);
            }
            if (backslashes > 0) { sb.Append('\\', backslashes * 2); }   // 结尾反斜杠翻倍，避免吃掉收尾引号
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// 校验用户提供的网址是否可安全地交给 yt-dlp。
        /// 只允许 http / https 开头，且不含双引号、空白与控制字符 ——
        /// 这些字符能突破引号边界变成额外参数。注意 `&amp;` `%` 是合法 URL 字符，不能一刀切禁掉。
        /// </summary>
        public static bool IsSafeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) { return false; }
            string s = url.Trim();
            if (!(s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) { return false; }
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || char.IsWhiteSpace(c) || char.IsControl(c)) { return false; }
            }
            return true;
        }

        private static string BuildSuffix(double start, double dur)
        {
            if (dur <= 0) { return "_完整音频"; }
            if (start <= 0) { return "_前" + Fmt.Num(dur) + "秒"; }
            return "_" + Fmt.Num(start) + "s-" + Fmt.Num(start + dur) + "s";
        }

        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) { return path; }
            string dir = Path.GetDirectoryName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; i < 1000; i++)
            {
                string p = Path.Combine(dir, baseName + " (" + i + ")" + ext);
                if (!File.Exists(p)) { return p; }
            }
            return Path.Combine(dir, baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
        }

        private static string BuildFfmpegArgs(string src, string dst, string ext, double? ss, double? t)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel error -nostdin -y ");
            if (ss.HasValue) { sb.Append("-ss ").Append(Fmt.Num(ss.Value)).Append(' '); }
            if (t.HasValue) { sb.Append("-t ").Append(Fmt.Num(t.Value)).Append(' '); }
            sb.Append("-i ").Append(Quote(src)).Append(" -vn ");
            if (ext == "wav")
            {
                sb.Append("-c:a pcm_s16le ");
            }
            else if (ext == "m4a")
            {
                string se = Path.GetExtension(src) == null ? "" : Path.GetExtension(src).ToLowerInvariant();
                if (se == ".m4a" || se == ".mp4" || se == ".aac") { sb.Append("-c:a copy "); }
                else { sb.Append("-c:a aac -b:a 192k "); }
            }
            else
            {
                sb.Append("-c:a libmp3lame -b:a 192k ");
            }
            sb.Append(Quote(dst));
            return sb.ToString();
        }

        private int RunYtDlp(string args)
        {
            return RunProcess(Tools.YtDlpPath, args, OnYtDlpLine);
        }

        private int RunFfmpeg(string args, out string note)
        {
            StringBuilder sb = new StringBuilder();
            _ffmpegNote = sb;
            int code = RunProcess(Tools.FfmpegPath, args, OnFfmpegLine);
            note = sb.ToString().Trim();
            return code;
        }

        public int RunFfmpegPublic(string args, out string note) { return RunFfmpeg(args, out note); }

        private StringBuilder _ffmpegNote;

        private void OnYtDlpLine(string line)
        {
            Match m = Regex.Match(line, @"^\[download\]\s+([0-9]{1,3}(?:\.[0-9]+)?)%");
            if (m.Success)
            {
                double pct;
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out pct))
                {
                    int v = (int)Math.Round(pct);
                    if (v < 0) { v = 0; }
                    if (v > 100) { v = 100; }
                    EmitStatus("下载中 " + v + "%");
                    EmitProgress(v);
                    return;
                }
            }
            if (line.Trim().Length == 0) { return; }
            bool keep = !line.StartsWith("[download]")
                        || line.IndexOf("Destination", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("Merging", StringComparison.OrdinalIgnoreCase) >= 0;
            if (keep) { EmitLog(line); }
        }

        private void OnFfmpegLine(string line)
        {
            if (line.Trim().Length == 0) { return; }
            if (_ffmpegNote != null && _ffmpegNote.Length < 2000) { _ffmpegNote.AppendLine(line); }
            EmitLog("[ffmpeg] " + line);
        }

        private int RunProcess(string exe, string args, Action<string> onLine)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                DataReceivedEventHandler handler = delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null && onLine != null) { onLine(e.Data); }
                };
                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;
                lock (_lock) { _proc = p; }
                try
                {
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    return p.ExitCode;
                }
                catch (Exception ex)
                {
                    EmitLog("调用 " + Path.GetFileName(exe) + " 失败：" + ex.Message);
                    return -1;
                }
                finally
                {
                    lock (_lock) { _proc = null; }
                }
            }
        }

        private void ThrowIfCancelled()
        {
            if (_cancel) { throw new OperationCanceledException("用户已取消。"); }
        }

        private void EmitLog(string s) { Action<string> h = Log; if (h != null) { h(s); } }
        private void EmitStatus(string s) { Action<string> h = Status; if (h != null) { h(s); } }
        private void EmitProgress(int p) { Action<int> h = Progress; if (h != null) { h(p); } }

        public static double ProbeDuration(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Tools.FfmpegPath, "-hide_banner -i " + Quote(path));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Match m = Regex.Match(err, @"Duration:\s*(\d+):(\d{1,2}):(\d{1,2}(?:\.\d+)?)");
                    if (m.Success)
                    {
                        double h = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        double mi = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        double se = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                        return h * 3600 + mi * 60 + se;
                    }
                }
            }
            catch (Exception) { }
            return 0;
        }
    }

    // ------------------------------------------------------------------
    //  主题：跟随系统浅色 / 深色
    // ------------------------------------------------------------------
    internal sealed class Theme
    {
        public bool Dark;

        public Color FormBack, Text, Muted, Accent;
        public Color InputBack, InputBorder, ButtonBack, ButtonBorder, LogBack;
        public Color WaveBack, WaveBar, Ruler, RulerText;
        public Color SelEdge, Handle, Playhead;
        public Color ProgressBar, ProgressTrack, ProgressBorder;
        public int DimAlpha;
        public Color DimColor;
        public int SelFillAlpha;

        /// <summary>自检用：强制指定主题；null 表示跟随系统。</summary>
        public static bool? Force;

        public static bool SystemIsDark()
        {
            if (Force.HasValue) { return Force.Value; }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) { return ((int)v) == 0; }
                }
            }
            catch (Exception) { }
            return false;
        }

        public static Theme Current()
        {
            return SystemIsDark() ? DarkTheme() : LightTheme();
        }

        public static Theme LightTheme()
        {
            Theme t = new Theme();
            t.Dark = false;
            t.FormBack = Color.FromArgb(247, 248, 250);
            t.Text = Color.FromArgb(32, 34, 38);
            t.Muted = Color.FromArgb(110, 112, 122);
            t.Accent = Color.FromArgb(28, 90, 175);
            t.InputBack = Color.White;
            t.InputBorder = Color.FromArgb(190, 194, 202);
            t.ButtonBack = Color.FromArgb(240, 241, 244);
            t.ButtonBorder = Color.FromArgb(170, 175, 185);
            t.LogBack = Color.White;
            t.WaveBack = Color.FromArgb(250, 251, 253);
            t.WaveBar = Color.FromArgb(72, 138, 216);
            t.Ruler = Color.FromArgb(196, 201, 210);
            t.RulerText = Color.FromArgb(110, 116, 126);
            t.SelEdge = Color.FromArgb(240, 160, 0);
            t.Handle = Color.FromArgb(240, 160, 0);
            t.Playhead = Color.FromArgb(220, 60, 60);
            t.ProgressBar = Color.FromArgb(64, 148, 236);
            t.ProgressTrack = Color.FromArgb(228, 231, 236);
            t.ProgressBorder = Color.FromArgb(196, 201, 210);
            t.DimAlpha = 130;
            t.DimColor = Color.White;
            t.SelFillAlpha = 55;
            return t;
        }

        public static Theme DarkTheme()
        {
            Theme t = new Theme();
            t.Dark = true;
            t.FormBack = Color.FromArgb(32, 34, 39);
            t.Text = Color.FromArgb(232, 234, 238);
            t.Muted = Color.FromArgb(150, 155, 165);
            t.Accent = Color.FromArgb(104, 172, 255);
            t.InputBack = Color.FromArgb(45, 48, 54);
            t.InputBorder = Color.FromArgb(74, 79, 88);
            t.ButtonBack = Color.FromArgb(56, 60, 68);
            t.ButtonBorder = Color.FromArgb(84, 89, 99);
            t.LogBack = Color.FromArgb(24, 26, 30);
            t.WaveBack = Color.FromArgb(20, 22, 26);
            t.WaveBar = Color.FromArgb(96, 168, 255);
            t.Ruler = Color.FromArgb(64, 70, 80);
            t.RulerText = Color.FromArgb(158, 165, 178);
            t.SelEdge = Color.FromArgb(255, 196, 0);
            t.Handle = Color.FromArgb(255, 196, 0);
            t.Playhead = Color.FromArgb(255, 90, 90);
            t.ProgressBar = Color.FromArgb(72, 156, 240);
            t.ProgressTrack = Color.FromArgb(48, 52, 60);
            t.ProgressBorder = Color.FromArgb(70, 75, 84);
            t.DimAlpha = 160;
            t.DimColor = Color.Black;
            t.SelFillAlpha = 46;
            return t;
        }

        public Color RoleColor(string role)
        {
            if (role == "muted") { return Muted; }
            if (role == "accent") { return Accent; }
            return Text;
        }

        /// <summary>把主题应用到整棵控件树。</summary>
        public void Apply(Control root)
        {
            Form f = root as Form;
            if (f != null) { f.BackColor = FormBack; f.ForeColor = Text; }

            GroupBox gb = root as GroupBox;
            if (gb != null) { gb.BackColor = FormBack; gb.ForeColor = Text; }

            Label l = root as Label;
            if (l != null)
            {
                l.BackColor = Color.Transparent;
                l.ForeColor = RoleColor(Convert.ToString(l.Tag));
            }

            TextBox tb = root as TextBox;
            if (tb != null)
            {
                bool isLog = Convert.ToString(tb.Tag) == "log";
                tb.BackColor = isLog ? LogBack : InputBack;
                tb.ForeColor = Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
            }

            ComboBox cb = root as ComboBox;
            if (cb != null)
            {
                cb.BackColor = InputBack;
                cb.ForeColor = Text;
                cb.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard;
            }

            NumericUpDown nu = root as NumericUpDown;
            if (nu != null)
            {
                nu.BackColor = InputBack;
                nu.ForeColor = Text;
                nu.BorderStyle = BorderStyle.FixedSingle;
            }

            Button b = root as Button;
            if (b != null)
            {
                if (Dark)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.UseVisualStyleBackColor = false;
                    b.BackColor = ButtonBack;
                    b.ForeColor = Text;
                    b.FlatAppearance.BorderColor = ButtonBorder;
                    b.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 75, 85);
                    b.FlatAppearance.MouseDownBackColor = Color.FromArgb(46, 50, 58);
                }
                else
                {
                    b.FlatStyle = FlatStyle.System;
                    b.UseVisualStyleBackColor = true;
                    b.BackColor = SystemColors.Control;
                    b.ForeColor = SystemColors.ControlText;
                }
            }

            FlatProgress fp = root as FlatProgress;
            if (fp != null) { fp.ApplyTheme(this); }

            WaveformView wv = root as WaveformView;
            if (wv != null) { wv.ApplyTheme(this); }

            foreach (Control child in root.Controls) { Apply(child); }
        }
    }

    // ------------------------------------------------------------------
    //  进度条：自绘（系统 ProgressBar 无法跟随深色主题）
    // ------------------------------------------------------------------
    internal sealed class FlatProgress : Control
    {
        private int _value;
        private bool _marquee;
        private int _offset;
        private readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer();

        private Color _bar = Color.FromArgb(64, 148, 236);
        private Color _track = Color.FromArgb(228, 231, 236);
        private Color _border = Color.FromArgb(196, 201, 210);

        public FlatProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _anim.Interval = 30;
            _anim.Tick += delegate(object s, EventArgs e)
            {
                _offset = (_offset + 6) % 120;
                Invalidate();
            };
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < 0) { v = 0; }
                if (v > 100) { v = 100; }
                if (v == _value) { return; }
                _value = v;
                Invalidate();
            }
        }

        public bool Marquee
        {
            get { return _marquee; }
            set
            {
                if (_marquee == value) { return; }
                _marquee = value;
                if (value) { _anim.Start(); } else { _anim.Stop(); }
                Invalidate();
            }
        }

        public void ApplyTheme(Theme t)
        {
            _bar = t.ProgressBar;
            _track = t.ProgressTrack;
            _border = t.ProgressBorder;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle rc = ClientRectangle;
            using (SolidBrush tb = new SolidBrush(_track)) { g.FillRectangle(tb, rc); }

            if (_marquee)
            {
                int w = Math.Max(40, rc.Width / 4);
                int x = (int)((long)(rc.Width + w) * _offset / 120) - w;
                Rectangle r = Rectangle.Intersect(new Rectangle(x, 0, w, rc.Height), rc);
                using (SolidBrush b = new SolidBrush(_bar)) { g.FillRectangle(b, r); }
            }
            else if (_value > 0)
            {
                int w = (int)Math.Round(rc.Width * (_value / 100.0));
                using (SolidBrush b = new SolidBrush(_bar)) { g.FillRectangle(b, 0, 0, w, rc.Height); }
            }

            using (Pen p = new Pen(_border))
            {
                g.DrawRectangle(p, 0, 0, rc.Width - 1, rc.Height - 1);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _anim.Dispose(); }
            base.Dispose(disposing);
        }
    }
    // ------------------------------------------------------------------
    //  波形控件：显示波形 + 选区操作杆 + 播放头，可拖动
    // ------------------------------------------------------------------
    internal sealed class WaveformView : Control
    {
        public float[] Peaks;
        public double DecodedFraction = 1;   // 已解码进度 0..1；<1 时只画到该比例（渐进绘制）
        public double Duration;
        public double SelStart;
        public double SelEnd;
        public double Position;
        public bool HasAudio;

        public double ViewStart;      // 可见时间窗
        public double ViewEnd;
        public double PeakStart;      // Peaks 覆盖的时间范围
        public double PeakEnd;

        public event EventHandler SelectionChanged;
        public event EventHandler SeekRequested;
        public event EventHandler ViewChanged;

        private enum Drag { None, Start, End, Body, Playhead }
        private Drag _drag = Drag.None;
        private double _dragOffset;
        private bool _panning;
        private int _panX;
        private double _panA, _panB;
        private bool _viewChangedDuringDrag;   // 交互期间视图变过 → 结束后需补一次包络重算

        private const int Pad = 10;
        private const int RulerH = 20;
        private const int Grab = 7;
        private Theme _theme = Theme.LightTheme();

        public WaveformView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            BackColor = _theme.WaveBack;
            Cursor = Cursors.Hand;
        }

        public void ApplyTheme(Theme t)
        {
            _theme = t;
            BackColor = t.WaveBack;
            Invalidate();
        }

        private double Span
        {
            get
            {
                double s = ViewEnd - ViewStart;
                return s <= 0.001 ? 1 : s;
            }
        }

        public void ResetView(double duration)
        {
            Duration = duration;
            ViewStart = 0;
            ViewEnd = duration <= 0 ? 1 : duration;
            PeakStart = 0;
            PeakEnd = ViewEnd;
        }

        private void SetView(double a, double b)
        {
            if (Duration <= 0) { return; }
            double minSpan = 0.5;
            if (b - a < minSpan) { b = a + minSpan; }
            if (a < 0) { b -= a; a = 0; }
            if (b > Duration) { a -= (b - Duration); b = Duration; }
            if (a < 0) { a = 0; }
            if (b - a > Duration) { b = a + Duration; }
            if (Math.Abs(a - ViewStart) < 0.0005 && Math.Abs(b - ViewEnd) < 0.0005) { return; }
            ViewStart = a;
            ViewEnd = b;
            if (IsInteracting) { _viewChangedDuringDrag = true; }
            EventHandler h = ViewChanged;
            if (h != null) { h(this, EventArgs.Empty); }
            Invalidate();
        }

        /// <summary>以 center 为中心缩放（factor&gt;1 放大）。</summary>
        public void ZoomAt(double factor, double center)
        {
            if (Duration <= 0) { return; }
            double span = Span / factor;
            if (span > Duration) { span = Duration; }
            if (span < 0.5) { span = 0.5; }
            double a = center - span / 2.0;
            SetView(a, a + span);
        }

        public void FitAll() { if (Duration > 0) { SetView(0, Duration); } }

        /// <summary>是否正处于交互中（右键平移，或拖动操作杆）。</summary>
        public bool IsInteracting { get { return _panning || _drag != Drag.None; } }

        /// <summary>自检用：模拟一次「右键拖动」平移，走与真实拖动相同的分支。</summary>
        internal void SimulatePanStep(double dt)
        {
            bool saved = _panning;
            _panning = true;
            try { SetView(ViewStart + dt, ViewEnd + dt); }
            finally { _panning = saved; }
        }

        public void FitSelection()
        {
            if (Duration <= 0) { return; }
            double len = SelEnd - SelStart;
            if (len <= 0.05) { len = 0.05; }
            double margin = Math.Max(1.0, len * 0.35);
            double a = SelStart - margin;
            double b = SelEnd + margin;
            if (a < 0) { a = 0; }
            if (b > Duration) { b = Duration; }
            SetView(a, b);
        }

        private double TimeAt(int x)
        {
            int w = ClientSize.Width - Pad * 2;
            if (w <= 10 || Duration <= 0) { return 0; }
            double t = ViewStart + (x - Pad) / (double)w * Span;
            if (t < 0) { t = 0; }
            if (t > Duration) { t = Duration; }
            return t;
        }

        private int XAt(double t)
        {
            int w = ClientSize.Width - Pad * 2;
            if (Duration <= 0) { return Pad; }
            return Pad + (int)Math.Round((t - ViewStart) / Span * w);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!HasAudio || Duration <= 0) { return; }
            Focus();
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                _panning = true;
                _panX = e.X;
                _panA = ViewStart;
                _panB = ViewEnd;
                Cursor = Cursors.SizeAll;
                return;
            }
            int xs = XAt(SelStart), xe = XAt(SelEnd);
            if (Math.Abs(e.X - xs) <= Grab)
            {
                _drag = Drag.Start;
            }
            else if (Math.Abs(e.X - xe) <= Grab)
            {
                _drag = Drag.End;
            }
            else if (e.X > xs && e.X < xe)
            {
                _drag = Drag.Body;
                _dragOffset = TimeAt(e.X) - SelStart;
            }
            else
            {
                _drag = Drag.Playhead;
                Position = TimeAt(e.X);
                RaiseSeek();
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!HasAudio) { return; }
            double t = TimeAt(e.X);
            ZoomAt(e.Delta > 0 ? 1.5 : (1.0 / 1.5), t);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!HasAudio || Duration <= 0) { return; }

            if (_panning)
            {
                int w = Math.Max(10, ClientSize.Width - Pad * 2);
                double dt = -(e.X - _panX) / (double)w * (_panB - _panA);
                SetView(_panA + dt, _panB + dt);
                return;
            }

            if (_drag != Drag.None)
            {
                double t = TimeAt(e.X);
                if (_drag == Drag.Start)
                {
                    SelStart = Math.Min(t, SelEnd - 0.05);
                    if (SelStart < 0) { SelStart = 0; }
                }
                else if (_drag == Drag.End)
                {
                    SelEnd = Math.Max(t, SelStart + 0.05);
                    if (SelEnd > Duration) { SelEnd = Duration; }
                }
                else if (_drag == Drag.Body)
                {
                    double len = SelEnd - SelStart;
                    double ns = t - _dragOffset;
                    if (ns < 0) { ns = 0; }
                    if (ns + len > Duration) { ns = Duration - len; }
                    SelStart = ns;
                    SelEnd = ns + len;
                }
                else
                {
                    Position = t;
                    RaiseSeek();
                }
                RaiseSelection();
                Invalidate();
                return;
            }

            int xs2 = XAt(SelStart), xe2 = XAt(SelEnd);
            if (Math.Abs(e.X - xs2) <= Grab || Math.Abs(e.X - xe2) <= Grab) { Cursor = Cursors.SizeWE; }
            else if (e.X > xs2 && e.X < xe2) { Cursor = Cursors.SizeAll; }
            else { Cursor = Cursors.Hand; }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool restore = _viewChangedDuringDrag;
            _drag = Drag.None;
            _panning = false;
            _viewChangedDuringDrag = false;
            if (restore)
            {
                // 交互结束：按最终视图补一次包络重算（拖动过程中刻意跳过了，以保证手感）
                EventHandler h = ViewChanged;
                if (h != null) { h(this, EventArgs.Empty); }
            }
        }

        private void RaiseSelection() { EventHandler h = SelectionChanged; if (h != null) { h(this, EventArgs.Empty); } }
        private void RaiseSeek() { EventHandler h = SeekRequested; if (h != null) { h(this, EventArgs.Empty); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle rc = ClientRectangle;
            using (SolidBrush bg = new SolidBrush(BackColor)) { g.FillRectangle(bg, rc); }

            int plotTop = 8;
            int plotBottom = rc.Height - RulerH - 4;
            int w = rc.Width - Pad * 2;
            if (w <= 10 || plotBottom <= plotTop) { return; }

            using (Pen border = new Pen(_theme.Ruler))
            {
                g.DrawRectangle(border, Pad - 1, plotTop - 1, w + 1, plotBottom - plotTop + 1);
            }

            if (!HasAudio || Peaks == null || Peaks.Length == 0 || Duration <= 0)
            {
                using (SolidBrush dim = new SolidBrush(_theme.Muted))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("尚未获取音频 —— 请先在第 1 步下载网址，或载入本地文件",
                                 Font, dim, new RectangleF(Pad, plotTop, w, plotBottom - plotTop), sf);
                }
                return;
            }

            int mid = (plotTop + plotBottom) / 2;
            int half = (plotBottom - plotTop) / 2 - 2;
            int n = Peaks.Length;
            double ps = PeakStart, pe = PeakEnd;
            if (pe <= ps) { pe = ps + 1; }

            // 只画「已解码」的那一段：解码是流式的，波形随解码进度从左往右长出来
            int drawnW = (int)Math.Round(w * (DecodedFraction < 0 ? 0 : (DecodedFraction > 1 ? 1 : DecodedFraction)));
            if (drawnW < 0) { drawnW = 0; }
            if (drawnW > w) { drawnW = w; }

            using (SolidBrush wave = new SolidBrush(_theme.WaveBar))
            {
                for (int x = 0; x < drawnW; x++)
                {
                    double t = ViewStart + (x / (double)w) * Span;
                    int i = (int)((t - ps) / (pe - ps) * n);
                    if (i < 0) { i = 0; }
                    if (i >= n) { i = n - 1; }
                    int h = (int)Math.Round(Peaks[i] * half);
                    if (h < 1) { h = 1; }
                    g.FillRectangle(wave, Pad + x, mid - h, 1, h * 2);
                }
            }

            // 尚未解码的部分画一条淡基线，示意"这里还没画完"
            if (drawnW < w)
            {
                using (Pen rest = new Pen(_theme.Ruler))
                {
                    g.DrawLine(rest, Pad + drawnW, mid, Pad + w, mid);
                }
            }

            int xs = XAt(SelStart), xe = XAt(SelEnd);
            if (xe - xs < 2) { xe = xs + 2; }

            using (SolidBrush shade = new SolidBrush(Color.FromArgb(_theme.DimAlpha, _theme.DimColor)))
            {
                int leftW = Math.Min(Math.Max(xs - Pad, 0), w);
                if (leftW > 0) { g.FillRectangle(shade, Pad, plotTop, leftW, plotBottom - plotTop); }
                int rightX = Math.Max(xe, Pad);
                if (rightX < Pad + w) { g.FillRectangle(shade, rightX, plotTop, Pad + w - rightX, plotBottom - plotTop); }
            }
            using (SolidBrush sel = new SolidBrush(Color.FromArgb(_theme.SelFillAlpha,
                                                                   _theme.SelEdge.R, _theme.SelEdge.G, _theme.SelEdge.B)))
            {
                int a = Math.Max(xs, Pad), b = Math.Min(xe, Pad + w);
                if (b > a) { g.FillRectangle(sel, a, plotTop, b - a, plotBottom - plotTop); }
            }
            using (Pen selPen = new Pen(_theme.SelEdge, 2f))
            {
                g.DrawRectangle(selPen, xs, plotTop, xe - xs, plotBottom - plotTop);
            }

            DrawHandle(g, xs, plotTop, plotBottom, true);
            DrawHandle(g, xe, plotTop, plotBottom, false);

            int xp = XAt(Position);
            if (xp >= Pad - 2 && xp <= Pad + w + 2)
            {
                using (Pen ph = new Pen(_theme.Playhead, 2f))
                {
                    g.DrawLine(ph, xp, plotTop - 4, xp, plotBottom + 4);
                }
                using (SolidBrush phb = new SolidBrush(_theme.Playhead))
                {
                    g.FillPolygon(phb, new Point[] {
                        new Point(xp - 5, plotTop - 4), new Point(xp + 5, plotTop - 4), new Point(xp, plotTop + 4)
                    });
                }
            }

            using (Pen tick = new Pen(_theme.Ruler))
            using (SolidBrush tl = new SolidBrush(_theme.RulerText))
            {
                double step = NiceStep(Span, w);
                double first = Math.Ceiling(ViewStart / step) * step;
                for (double t = first; t <= ViewEnd + 0.0001; t += step)
                {
                    int x = XAt(t);
                    g.DrawLine(tick, x, plotBottom + 1, x, plotBottom + 5);
                    string s = Fmt.Len(t);
                    SizeF sz = g.MeasureString(s, Font);
                    float tx = x - sz.Width / 2;
                    if (tx < 0) { tx = 0; }
                    if (tx + sz.Width > rc.Width) { tx = rc.Width - sz.Width; }
                    g.DrawString(s, Font, tl, tx, plotBottom + 6);
                }
            }
        }

        private void DrawHandle(Graphics g, int x, int top, int bottom, bool isStart)
        {
            using (SolidBrush b = new SolidBrush(_theme.Handle))
            {
                g.FillRectangle(b, x - 3, top, 6, bottom - top);
                Point[] tri = isStart
                    ? new Point[] { new Point(x, top), new Point(x + 9, top), new Point(x, top + 9) }
                    : new Point[] { new Point(x, top), new Point(x - 9, top), new Point(x, top + 9) };
                g.FillPolygon(b, tri);
            }
        }

        private static double NiceStep(double duration, int width)
        {
            double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
            double target = duration / Math.Max(2.0, width / 90.0);
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] >= target) { return steps[i]; }
            }
            return steps[steps.Length - 1];
        }
    }

    // ------------------------------------------------------------------
    //  播放器统一接口：MCI（WAV）与 WPF MediaPlayer（m4a / mp3 等）都实现它
    // ------------------------------------------------------------------
    internal interface IAudioPlayer
    {
        bool IsOpen { get; }
        bool IsPlaying { get; }
        double LengthMs { get; }
        double PositionMs { get; }
        /// <summary>最近一次播放失败的原因（无错误时为空串）。</summary>
        string LastError { get; }
        string Open(string path);
        void PlayFrom(double ms);
        void PlayRange(double fromMs, double toMs);
        void Pause();
        void Stop();
        void Seek(double ms);
        void Close();
    }

    // ------------------------------------------------------------------
    //  播放器：winmm MCI（waveaudio），支持定位 / 区间播放
    // ------------------------------------------------------------------
    internal sealed class MciPlayer : IDisposable, IAudioPlayer
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int mciSendString(string command, StringBuilder ret, int retLen, IntPtr hwnd);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool mciGetErrorString(int err, StringBuilder buf, int len);

        private const string Alias = "maepreview";
        private bool _open;

        public bool IsOpen { get { return _open; } }
        public double LengthMs { get; private set; }
        /// <summary>MCI 的失败都通过 Open 的返回值上报，这里恒为空串。</summary>
        public string LastError { get { return ""; } }

        private static int Raw(string cmd, out string ret)
        {
            StringBuilder sb = new StringBuilder(256);
            int r = mciSendString(cmd, sb, sb.Capacity, IntPtr.Zero);
            ret = sb.ToString();
            return r;
        }

        private static string ErrorText(int code)
        {
            StringBuilder sb = new StringBuilder(256);
            mciGetErrorString(code, sb, sb.Capacity);
            string s = sb.ToString();
            return s.Length > 0 ? s : ("MCI 错误 " + code);
        }

        /// <summary>打开 WAV；成功返回 null，失败返回错误说明。</summary>
        public string Open(string wavPath)
        {
            Close();
            string ret;
            int r = Raw("open \"" + wavPath + "\" type waveaudio alias " + Alias, out ret);
            if (r != 0) { return ErrorText(r); }
            _open = true;
            Raw("set " + Alias + " time format milliseconds", out ret);
            double len = 0;
            if (Raw("status " + Alias + " length", out ret) == 0)
            {
                double.TryParse(ret.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out len);
            }
            LengthMs = len;
            return null;
        }

        public double PositionMs
        {
            get
            {
                if (!_open) { return 0; }
                string ret;
                double v = 0;
                if (Raw("status " + Alias + " position", out ret) == 0)
                {
                    double.TryParse(ret.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
                }
                return v;
            }
        }

        public bool IsPlaying
        {
            get
            {
                if (!_open) { return false; }
                string ret;
                if (Raw("status " + Alias + " mode", out ret) != 0) { return false; }
                return ret.Trim() == "playing";
            }
        }

        public void PlayFrom(double ms)
        {
            if (!_open) { return; }
            string ret;
            int m = (int)Math.Max(0, ms);
            Raw("play " + Alias + " from " + m, out ret);
        }

        public void PlayRange(double fromMs, double toMs)
        {
            if (!_open) { return; }
            string ret;
            int a = (int)Math.Max(0, fromMs);
            int b = (int)Math.Max(a + 50, toMs);
            Raw("play " + Alias + " from " + a + " to " + b, out ret);
        }

        public void Pause()
        {
            if (!_open) { return; }
            string ret;
            Raw("pause " + Alias, out ret);
        }

        public void Stop()
        {
            if (!_open) { return; }
            string ret;
            Raw("stop " + Alias, out ret);
            Raw("seek " + Alias + " to 0", out ret);
        }

        public void Seek(double ms)
        {
            if (!_open) { return; }
            string ret;
            Raw("seek " + Alias + " to " + (int)Math.Max(0, ms), out ret);
        }

        public void Close()
        {
            if (!_open) { return; }
            string ret;
            Raw("stop " + Alias, out ret);
            Raw("close " + Alias, out ret);
            _open = false;
        }

        public void Dispose() { Close(); }
    }

    // ------------------------------------------------------------------
    //  播放器：winmm waveOut + 内置 ffmpeg 流式解码
    //
    //  为什么不用系统现成的播放能力：
    //    · MCI 的 waveaudio 设备只认 WAV —— 原文件若是 m4a / mp3 就打不开
    //    · WPF 的 MediaPlayer 实测打不开 m4a（它走 WMP 那套栈，MP4/AAC 支持不可靠）
    //  所以这里用「ffmpeg 解码 → stdout → waveOut 流式输出」：
    //  格式支持完全交给 ffmpeg（本来就要内置它），输出只依赖系统自带的 winmm，
    //  依然不引入任何第三方音频库。
    // ------------------------------------------------------------------
    internal sealed class WaveOutPlayer : IDisposable, IAudioPlayer
    {
        private const int Rate = 44100;                 // 统一输出 44.1kHz 单声道 16bit
        private const int ChunkMs = 80;                 // 每块 80ms
        private const int ChunkSamples = Rate * ChunkMs / 1000;
        private const int ChunkBytes = ChunkSamples * 2;
        private const int ChunkCount = 3;               // 三缓冲，避免块间断音
        private const int JoinTimeoutMs = 2000;

        private const uint WAVE_MAPPER = 0xFFFFFFFF;
        private const uint CALLBACK_NULL = 0;
        private const uint TIME_MS = 0x0001;
        private const uint TIME_SAMPLES = 0x0002;
        private const uint TIME_BYTES = 0x0004;
        private const uint WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        /// <summary>
        /// MMTIME 的真实布局是 wType + 4 字节 union = **8 字节**。
        /// （早先多声明了 4 字节填充，靠 API 容忍超长结构才没出问题，这里改成真实布局。）
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MMTIME
        {
            public uint wType;
            public uint val;
        }

        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, uint dev, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] private static extern int waveOutPause(IntPtr hwo);
        [DllImport("winmm.dll")] private static extern int waveOutRestart(IntPtr hwo);
        [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr hwo, ref MMTIME t, int size);

        private string _path;
        private double _lengthMs;
        private double _startMs;          // 本次播放的起点（毫秒）
        private string _lastError = "";
        private IntPtr _hwo = IntPtr.Zero;
        private IntPtr[] _bufs;           // 每块的数据内存
        private IntPtr[] _hdrs;           // 每块的 WAVEHDR
        private int _hdrSize;
        private Process _proc;
        private Thread _feeder;
        private System.Windows.Forms.Timer _rangeTimer;
        private double _rangeEndMs = -1;

        private volatile bool _stop;
        private volatile bool _eof;
        private volatile bool _playing;
        private volatile bool _paused;
        /// <summary>
        /// 每次 Start 自增。喂数线程启动时记下自己的代号，之后每次写缓冲前都核对一次；
        /// 一旦不相等就说明「这次播放已经被新的播放取代」，必须立刻退出，绝不能再去碰
        /// 已经易主的缓冲或设备句柄。
        /// </summary>
        private volatile int _generation;
        private long _written;            // 已送入设备的采样数（用 Interlocked 访问）

        public bool IsOpen { get { return _path != null; } }
        public double LengthMs { get { return _lengthMs; } }
        public string LastError { get { return _lastError; } }

        public double PositionMs
        {
            get
            {
                if (_path == null) { return 0; }
                if (!_playing && !_paused) { return _startMs; }
                double ms = _startMs + PlayedSamples(_hwo) * 1000.0 / Rate;
                if (_lengthMs > 0 && ms > _lengthMs) { ms = _lengthMs; }
                return ms;
            }
        }

        public bool IsPlaying
        {
            get
            {
                if (!_playing || _paused) { return false; }
                if (_eof && PlayedSamples(_hwo) >= Interlocked.Read(ref _written)) { return false; }
                return true;
            }
        }

        public string Open(string path) { return Open(path, 0); }

        /// <summary>
        /// 只探测文件可读性与时长；真正解码在按下播放时才开始。
        /// durationHint &gt; 0 时直接采用（调用方已经知道时长），
        /// 避免在 UI 线程里再同步跑一次 ffmpeg 探测导致卡顿。
        /// </summary>
        public string Open(string path, double durationHint)
        {
            Close();
            _lastError = "";
            if (string.IsNullOrEmpty(path)) { _lastError = "未提供文件。"; return _lastError; }
            if (!File.Exists(path)) { _lastError = "找不到文件：" + path; return _lastError; }
            double dur = durationHint;
            if (dur <= 0.05) { dur = Extractor.ProbeDuration(path); }
            if (dur <= 0.05) { _lastError = "无法读取该文件（时长无效）。"; return _lastError; }
            _path = path;
            _lengthMs = dur * 1000.0;
            _startMs = 0;
            return null;
        }

        public void PlayFrom(double ms) { Start(ms, -1); }

        public void PlayRange(double fromMs, double toMs) { Start(fromMs, toMs); }

        public void Pause()
        {
            if (_hwo == IntPtr.Zero || !_playing || _paused) { return; }
            try { waveOutPause(_hwo); } catch (Exception) { }
            _paused = true;
        }

        public void Stop()
        {
            StopInternal();
            _startMs = 0;
        }

        public void Seek(double ms)
        {
            if (_path == null) { return; }
            if (_playing || _paused) { Start(ms, -1); }
            else { _startMs = Math.Max(0, ms); }
        }

        public void Close()
        {
            StopInternal();
            _path = null;
            _lengthMs = 0;
            _startMs = 0;
        }

        public void Dispose() { Close(); }

        // ---------------- 内部 ----------------

        /// <summary>
        /// 设备已播放的采样数。
        /// 必须先把 MMTIME.wType 设成 TIME_SAMPLES —— 否则设备会按自己的默认格式返回
        /// （实测返回的是字节数），把字节当采样用会让播放头走得快一倍。
        /// </summary>
        private static long PlayedSamples(IntPtr hwo)
        {
            if (hwo == IntPtr.Zero) { return 0; }
            try
            {
                MMTIME t = new MMTIME();
                t.wType = TIME_SAMPLES;
                if (waveOutGetPosition(hwo, ref t, Marshal.SizeOf(typeof(MMTIME))) != 0) { return 0; }
                if (t.wType == TIME_BYTES) { return (long)t.val / 2; }                   // 单声道 16bit：2 字节/采样
                if (t.wType == TIME_MS) { return (long)(t.val * (Rate / 1000.0)); }       // 毫秒兜底
                return t.val;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// 某块缓冲是否已播完、可以复用。
        /// 主判据是 waveOutGetPosition 的推进；但该 API 在个别驱动上会恒返回 0，
        /// 那样喂满 3 块（240ms）之后就会永久卡死，所以再补一条 WAVEHDR 的 WHDR_DONE 判据。
        /// </summary>
        private static bool BufferFree(IntPtr hwo, IntPtr hdr, long startSample)
        {
            if (startSample < 0) { return true; }                 // 从未用过
            WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(hdr, typeof(WAVEHDR));
            if ((h.dwFlags & WHDR_DONE) != 0) { return true; }    // 驱动已交回
            return PlayedSamples(hwo) >= startSample + ChunkSamples;
        }

        private void StopInternal()
        {
            _stop = true;
            _generation++;              // 让仍在运行的喂数线程立刻失效
            _playing = false;
            _paused = false;
            StopRangeTimer();

            // 先杀 ffmpeg：它一停，阻塞在读取上的喂数线程就会立刻返回
            if (_proc != null)
            {
                try { if (!_proc.HasExited) { _proc.Kill(); } } catch (Exception) { }
            }

            Thread f = _feeder;
            _feeder = null;
            bool exited = true;
            if (f != null)
            {
                try { exited = f.Join(JoinTimeoutMs); } catch (Exception) { }
            }

            if (exited)
            {
                if (_hwo != IntPtr.Zero)
                {
                    try { waveOutReset(_hwo); } catch (Exception) { }
                    if (_hdrs != null)
                    {
                        for (int i = 0; i < ChunkCount; i++)
                        {
                            if (_hdrs[i] != IntPtr.Zero)
                            {
                                try { waveOutUnprepareHeader(_hwo, _hdrs[i], _hdrSize); } catch (Exception) { }
                            }
                        }
                    }
                    try { waveOutClose(_hwo); } catch (Exception) { }
                    _hwo = IntPtr.Zero;
                }
                FreeBuffers();
            }
            else
            {
                // 喂数线程没能在超时内退出（例如驱动卡在 waveOutWrite 上）：
                // 此时**绝不能**释放它仍可能访问的非托管缓冲，否则就是 use-after-free。
                // 宁可弃置这一组缓冲（几百 KB，进程退出时由系统回收），也不能崩。
                _hwo = IntPtr.Zero;
                _bufs = null;
                _hdrs = null;
            }

            if (_proc != null)
            {
                try { _proc.Dispose(); } catch (Exception) { }
                _proc = null;
            }
            _eof = false;
            Interlocked.Exchange(ref _written, 0);
        }

        private void FreeBuffers()
        {
            if (_bufs != null)
            {
                for (int i = 0; i < ChunkCount; i++)
                {
                    if (_bufs[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_bufs[i]); _bufs[i] = IntPtr.Zero; }
                }
                _bufs = null;
            }
            if (_hdrs != null)
            {
                for (int i = 0; i < ChunkCount; i++)
                {
                    if (_hdrs[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_hdrs[i]); _hdrs[i] = IntPtr.Zero; }
                }
                _hdrs = null;
            }
        }

        private void Start(double fromMs, double toMs)
        {
            if (_path == null) { return; }
            StopInternal();
            _stop = false;
            _eof = false;
            Interlocked.Exchange(ref _written, 0);
            _startMs = Math.Max(0, fromMs);
            _lastError = "";

            WAVEFORMATEX fmt = new WAVEFORMATEX();
            fmt.wFormatTag = 1;                       // WAVE_FORMAT_PCM
            fmt.nChannels = 1;
            fmt.nSamplesPerSec = (uint)Rate;
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = (ushort)(fmt.nChannels * fmt.wBitsPerSample / 8);
            fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;

            IntPtr hwo;
            if (waveOutOpen(out hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL) != 0)
            {
                // 以前这里静默返回，导致「点了播放却毫无反应」；现在把原因交给界面显示
                _lastError = "无法打开音频输出设备（可能没有可用声卡，或被其它程序独占）。";
                return;
            }
            _hwo = hwo;

            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            IntPtr[] bufs = new IntPtr[ChunkCount];
            IntPtr[] hdrs = new IntPtr[ChunkCount];
            _hdrSize = hdrSize;
            _bufs = bufs;
            _hdrs = hdrs;

            Process proc = null;
            try
            {
                for (int i = 0; i < ChunkCount; i++)
                {
                    bufs[i] = Marshal.AllocHGlobal(ChunkBytes);
                    hdrs[i] = Marshal.AllocHGlobal(hdrSize);
                    WAVEHDR h = new WAVEHDR();
                    h.lpData = bufs[i];
                    h.dwBufferLength = (uint)ChunkBytes;
                    Marshal.StructureToPtr(h, hdrs[i], false);
                    waveOutPrepareHeader(hwo, hdrs[i], hdrSize);
                }

                string args = "-hide_banner -loglevel error -nostdin -ss " +
                              (_startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
                if (toMs > 0)
                {
                    args += " -t " + ((toMs - _startMs) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
                }
                args += " -i " + Extractor.Quote(_path) + " -vn -ac 1 -ar " + Rate + " -f s16le pipe:1";

                ProcessStartInfo psi = new ProcessStartInfo(Tools.FfmpegPath, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _lastError = "无法启动解码进程：" + ex.Message;
                if (proc != null) { try { if (!proc.HasExited) { proc.Kill(); } } catch (Exception) { } }
                StopInternal();
                return;
            }

            _proc = proc;
            _playing = true;
            _paused = false;
            if (toMs > 0) { _rangeEndMs = Math.Max(fromMs + 50, toMs); StartRangeTimer(); }

            int gen = ++_generation;
            Thread t = new Thread(delegate() { FeedLoop(gen, hwo, bufs, hdrs, proc); });
            t.IsBackground = true;
            _feeder = t;
            t.Start();
        }

        /// <summary>
        /// 喂数线程：从 ffmpeg 的 stdout 读 PCM，填进三块循环缓冲交给 waveOut。
        /// 缓冲与设备句柄都由**本线程自己持有**（启动时捕获），不再从字段读，
        /// 配合 generation 代号，避免与下一次播放争用同一组资源。
        /// </summary>
        private void FeedLoop(int gen, IntPtr hwo, IntPtr[] bufs, IntPtr[] hdrs, Process proc)
        {
            byte[] data = new byte[ChunkBytes];
            long[] bufStart = new long[ChunkCount];
            for (int i = 0; i < ChunkCount; i++) { bufStart[i] = -1; }
            int next = 0;
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));

            Stream input;
            try { input = proc.StandardOutput.BaseStream; }
            catch (Exception) { _eof = true; return; }

            while (!_stop && gen == _generation)
            {
                int i = next;
                if (!BufferFree(hwo, hdrs[i], bufStart[i]))
                {
                    Thread.Sleep(5);
                    continue;
                }

                int got;
                try { got = input.Read(data, 0, data.Length); }
                catch (Exception) { break; }
                if (got <= 0) { break; }
                if ((got & 1) == 1) { got--; }
                if (got <= 0) { continue; }

                // 已经过期（新的播放开始了）就立刻收手，绝不碰不属于自己的缓冲
                if (_stop || gen != _generation) { break; }

                Marshal.Copy(data, 0, bufs[i], got);
                WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(hdrs[i], typeof(WAVEHDR));
                h.dwBufferLength = (uint)got;
                // 只能清 WHDR_DONE：WHDR_PREPARED 是 waveOutPrepareHeader 设的，
                // 一起清掉会让 waveOutWrite 直接失败（缓冲永远播不出来）。
                h.dwFlags = h.dwFlags & ~WHDR_DONE;
                Marshal.StructureToPtr(h, hdrs[i], false);

                bufStart[i] = Interlocked.Read(ref _written);
                Interlocked.Add(ref _written, got / 2);

                if (waveOutWrite(hwo, hdrs[i], hdrSize) != 0)
                {
                    // 以前不检查返回值：写入失败时数据被丢，但 _written 仍自增，
                    // 会让播放按钮一直卡在「暂停」。现在直接结束并留下原因。
                    _lastError = "写入音频设备失败（waveOutWrite）。";
                    break;
                }
                next = (i + 1) % ChunkCount;
            }
            _eof = true;
        }

        private void StartRangeTimer()
        {
            if (_rangeTimer == null)
            {
                _rangeTimer = new System.Windows.Forms.Timer();
                _rangeTimer.Interval = 40;
                _rangeTimer.Tick += delegate(object s, EventArgs e) { OnRangeTick(); };
            }
            _rangeTimer.Start();
        }

        private void StopRangeTimer()
        {
            if (_rangeTimer != null) { _rangeTimer.Stop(); }
        }

        /// <summary>MediaPlayer 没有「播到某处自动停」，用定时器实现「只播放选区」。</summary>
        private void OnRangeTick()
        {
            if (!_playing || _rangeEndMs <= 0) { StopRangeTimer(); return; }
            if (PositionMs >= _rangeEndMs)
            {
                StopInternal();
                StopRangeTimer();
            }
        }
    }

    // ------------------------------------------------------------------
    //  播放器门面：按格式挑引擎，失败自动换另一条路。
    //  · WAV  → MCI（waveaudio，最稳）
    //  · 其它 → waveOut + ffmpeg（能直接播 m4a / mp3 等**原文件**，无需先转 WAV）
    // ------------------------------------------------------------------
    internal sealed class Player : IDisposable
    {
        private readonly MciPlayer _mci = new MciPlayer();
        private readonly WaveOutPlayer _wave = new WaveOutPlayer();
        private IAudioPlayer _active;

        public bool IsOpen { get { return _active != null && _active.IsOpen; } }
        public bool IsPlaying { get { return _active != null && _active.IsPlaying; } }
        public double LengthMs { get { return _active == null ? 0 : _active.LengthMs; } }
        public double PositionMs { get { return _active == null ? 0 : _active.PositionMs; } }
        public string LastError { get { return _active == null ? "" : _active.LastError; } }

        public string Open(string path) { return Open(path, 0); }

        /// <summary>durationHint &gt; 0 时直接采用，省掉一次同步的 ffmpeg 时长探测。</summary>
        public string Open(string path, double durationHint)
        {
            Close();
            bool wav = path != null && path.ToLowerInvariant().EndsWith(".wav");
            if (wav)
            {
                string e = _mci.Open(path);
                if (e == null) { _active = _mci; return null; }
            }
            string err = _wave.Open(path, durationHint);
            if (err == null) { _active = _wave; return null; }
            if (!wav)
            {
                string e2 = _mci.Open(path);
                if (e2 == null) { _active = _mci; return null; }
            }
            return err;
        }

        public void PlayFrom(double ms) { if (_active != null) { _active.PlayFrom(ms); } }
        public void PlayRange(double a, double b) { if (_active != null) { _active.PlayRange(a, b); } }
        public void Pause() { if (_active != null) { _active.Pause(); } }
        public void Stop() { if (_active != null) { _active.Stop(); } }
        public void Seek(double ms) { if (_active != null) { _active.Seek(ms); } }

        public void Close()
        {
            try { _mci.Close(); } catch (Exception) { }
            try { _wave.Close(); } catch (Exception) { }
            _active = null;
        }

        public void Dispose() { Close(); }
    }

    // ------------------------------------------------------------------
    //  主界面
    // ------------------------------------------------------------------
    internal sealed class MainForm : Form
    {
        // 第 1 步
        private TextBox _txtUrl;
        private Button _btnPaste;
        private ComboBox _cmbQuality;
        private TextBox _txtOut;
        private Button _btnBrowse;
        private Button _btnDownload;
        private Button _btnCancel;
        private FlatProgress _barDown;
        private Label _lblDownStatus;

        // 第 2 步
        private WaveformView _wave;
        private Button _btnPlay;
        private Button _btnStop;
        private Button _btnAudition;
        private Button _btnMarkStart;
        private Button _btnMarkEnd;
        private Label _lblTime;
        private NumericUpDown _numStart;
        private NumericUpDown _numEnd;
        private Label _lblSelLen;
        private Button _btnSelectAll;

        // 第 3 步
        private ComboBox _cmbFormat;
        private TextBox _txtName;
        private Button _btnExport;
        private Label _lblExportStatus;

        private TextBox _txtLog;

        private readonly Extractor _engine = new Extractor();
        private readonly Player _player = new Player();
        private readonly System.Windows.Forms.Timer _ticker = new System.Windows.Forms.Timer();

        private AudioSession _session;
        private string _workDir;
        private float[] _fullPeaks;
        private int _peakReloadCount;      // 自检用：包络重算（读盘）次数
        private volatile bool _busy;
        private volatile bool _cancel;
        private bool _syncing;
        private bool _nameDirty;
        private string _lastFile;

        public MainForm()
        {
            BuildUi();
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data == null) { return; }
                if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
                    e.Data.GetDataPresent(DataFormats.Text)) { e.Effect = DragDropEffects.Copy; }
            };
            DragDrop += delegate(object s, DragEventArgs e)
            {
                if (e.Data == null) { return; }
                // 从资源管理器拖进来的文件走 FileDrop，拖链接文本走 Text
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0) { _txtUrl.Text = files[0]; }
                }
                else if (e.Data.GetDataPresent(DataFormats.Text))
                {
                    _txtUrl.Text = Convert.ToString(e.Data.GetData(DataFormats.Text));
                }
            };

            // 输入是本地文件还是网址，决定按钮文案与「音质」是否可用
            _txtUrl.TextChanged += delegate(object s, EventArgs e) { UpdateSourceMode(); };

            string def = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(def) || !Directory.Exists(def))
            {
                def = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            _txtOut.Text = def;

            _ticker.Interval = 80;
            _ticker.Tick += OnTick;

            ApplyTheme(Theme.Current());
            SetBusy(false);
            UpdateUiState();
            _lblDownStatus.Text = "正在准备内置组件（首次运行需解压约 100 MB）…";
            ThreadPool.QueueUserWorkItem(delegate { PrepareTools(); });
        }

        // ---------------- 界面搭建 ----------------
        private void BuildUi()
        {
            Font uiFont;
            try { uiFont = new Font("Microsoft YaHei UI", 9F); }
            catch (Exception) { uiFont = SystemFonts.DefaultFont; }

            SuspendLayout();
            Text = "视频音频提取器 v" + Program.AppVersion + "  —  下载整段音频 → 拖动选区试听 → 裁剪保存";
            Font = uiFont;
            ClientSize = new Size(960, 680);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(940, 690);
            BackColor = Color.FromArgb(247, 248, 250);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            // ---------- 第 1 步 ----------
            GroupBox g1 = MakeGroup("第 1 步 · 获取完整音频（粘贴网址，或选择/拖入本地文件）", 12, 10, 936, 128);

            Label l1 = MakeLabel("网址/文件：", 16, 28);
            _txtUrl = new TextBox();
            _txtUrl.Location = new Point(100, 25);
            _txtUrl.Size = new Size(596, 23);
            _txtUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _btnPaste = MakeButton("粘贴链接/路径", 708, 24, 120, 25, AnchorStyles.Top | AnchorStyles.Right);
            _btnPaste.Click += OnPaste;

            Label l2 = MakeLabel("音质：", 16, 60);
            _cmbQuality = new ComboBox();
            _cmbQuality.Location = new Point(90, 57);
            _cmbQuality.Size = new Size(160, 23);
            _cmbQuality.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbQuality.BackColor = Color.White;
            _cmbQuality.Items.AddRange(new object[]
            {
                "最好音质（自动）", "标准（≤128 kbps）", "省流量（≤64 kbps）"
            });
            _cmbQuality.SelectedIndex = 0;

            Label l3 = MakeLabel("保存位置：", 262, 60);
            _txtOut = new TextBox();
            _txtOut.Location = new Point(336, 57);
            _txtOut.Size = new Size(360, 23);
            _txtOut.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _btnBrowse = MakeButton("浏览…", 708, 56, 96, 25, AnchorStyles.Top | AnchorStyles.Right);
            _btnBrowse.Click += OnBrowse;

            // 操作按钮统一靠右（贴合「确定 / 取消在右下」的习惯），进度条占据左侧剩余空间
            _btnDownload = MakeButton("① 下载完整音频", 706, 89, 140, 28, AnchorStyles.Top | AnchorStyles.Right);
            _btnDownload.Font = new Font(uiFont, FontStyle.Bold);
            _btnDownload.Click += OnDownload;

            _btnCancel = MakeButton("取消", 854, 89, 70, 28, AnchorStyles.Top | AnchorStyles.Right);
            _btnCancel.Click += OnCancel;

            _barDown = new FlatProgress();
            _barDown.Location = new Point(90, 94);
            _barDown.Size = new Size(400, 18);
            _barDown.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _lblDownStatus = new Label();
            _lblDownStatus.Text = "";
            _lblDownStatus.Location = new Point(498, 91);
            _lblDownStatus.Size = new Size(200, 20);
            _lblDownStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _lblDownStatus.Tag = "accent";
            _lblDownStatus.ForeColor = Color.FromArgb(30, 90, 170);

            g1.Controls.AddRange(new Control[] { l1, _txtUrl, _btnPaste, l2, _cmbQuality,
                                                 l3, _txtOut, _btnBrowse, _btnDownload, _btnCancel,
                                                 _barDown, _lblDownStatus });

            // ---------- 第 2 步 ----------
            GroupBox g2 = MakeGroup("第 2 步 · 试听与选取片段（波形上拖动左右操作杆）", 12, 146, 936, 330);

            _wave = new WaveformView();
            _wave.Location = new Point(20, 24);
            _wave.Size = new Size(896, 180);
            _wave.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _wave.SelectionChanged += OnWaveSelection;
            _wave.SeekRequested += OnWaveSeek;
            _wave.ViewChanged += OnWaveViewChanged;

            _btnPlay = MakeButton("▶ 播放", 20, 212, 92, 30, AnchorStyles.Top | AnchorStyles.Left);
            _btnPlay.Click += OnPlayPause;
            _btnStop = MakeButton("■ 停止", 118, 212, 82, 30, AnchorStyles.Top | AnchorStyles.Left);
            _btnStop.Click += OnStop;
            _btnAudition = MakeButton("试听选区", 206, 212, 104, 30, AnchorStyles.Top | AnchorStyles.Left);
            _btnAudition.Click += OnAudition;

            Button btnZoomOut = MakeButton("－ 缩小", 316, 212, 62, 30, AnchorStyles.Top | AnchorStyles.Left);
            btnZoomOut.Click += OnZoomOut;
            Button btnZoomIn = MakeButton("＋ 放大", 382, 212, 62, 30, AnchorStyles.Top | AnchorStyles.Left);
            btnZoomIn.Click += OnZoomIn;
            Button btnFitAll = MakeButton("全览", 448, 212, 62, 30, AnchorStyles.Top | AnchorStyles.Left);
            btnFitAll.Click += OnFitAll;

            _lblTime = new Label();
            _lblTime.Text = "--:--.- / --:--.-";
            _lblTime.Location = new Point(520, 218);
            _lblTime.Size = new Size(130, 20);

            _btnMarkStart = MakeButton("起点=播放头", 700, 212, 106, 30, AnchorStyles.Top | AnchorStyles.Right);
            _btnMarkStart.Click += OnMarkStart;
            _btnMarkEnd = MakeButton("终点=播放头", 812, 212, 106, 30, AnchorStyles.Top | AnchorStyles.Right);
            _btnMarkEnd.Click += OnMarkEnd;

            Label l4 = MakeLabel("起始", 20, 256);
            l4.Size = new Size(38, 22);
            _numStart = new NumericUpDown();
            _numStart.Location = new Point(60, 252);
            _numStart.Size = new Size(84, 23);
            _numStart.DecimalPlaces = 1;
            _numStart.Increment = 1;
            _numStart.Maximum = 100000;
            _numStart.BackColor = Color.White;
            _numStart.ValueChanged += OnSelectionNumbers;

            Label l5 = MakeLabel("秒", 148, 256);
            l5.Size = new Size(22, 22);
            Label l6 = MakeLabel("结束", 176, 256);
            l6.Size = new Size(38, 22);
            _numEnd = new NumericUpDown();
            _numEnd.Location = new Point(216, 252);
            _numEnd.Size = new Size(84, 23);
            _numEnd.DecimalPlaces = 1;
            _numEnd.Increment = 1;
            _numEnd.Maximum = 100000;
            _numEnd.BackColor = Color.White;
            _numEnd.ValueChanged += OnSelectionNumbers;

            Label l7 = MakeLabel("秒", 304, 256);
            l7.Size = new Size(22, 22);

            _lblSelLen = new Label();
            _lblSelLen.Text = "选区时长：0.0 秒";
            _lblSelLen.Location = new Point(340, 256);
            _lblSelLen.Size = new Size(170, 22);
            _lblSelLen.Tag = "accent";
            _lblSelLen.ForeColor = Color.FromArgb(30, 90, 170);

            _btnSelectAll = MakeButton("全选", 596, 250, 90, 30, AnchorStyles.Top | AnchorStyles.Right);
            _btnSelectAll.Click += OnSelectAll;
            Button btnFocus = MakeButton("聚焦选区", 694, 250, 100, 30, AnchorStyles.Top | AnchorStyles.Right);
            btnFocus.Click += OnFitSelection;
            Button btnZero = MakeButton("回到开头", 802, 250, 116, 30, AnchorStyles.Top | AnchorStyles.Right);
            btnZero.Click += OnGoStart;

            g2.Controls.AddRange(new Control[] { _wave, _btnPlay, _btnStop, _btnAudition, btnZoomOut, btnZoomIn,
                                                 btnFitAll, _lblTime, _btnMarkStart, _btnMarkEnd,
                                                 l4, _numStart, l5, l6, _numEnd, l7,
                                                 _lblSelLen, _btnSelectAll, btnFocus, btnZero });

            // ---------- 第 3 步 ----------
            GroupBox g3 = MakeGroup("第 3 步 · 裁剪并保存选区", 12, 484, 936, 96);

            Label l8 = MakeLabel("输出格式：", 16, 28);
            _cmbFormat = new ComboBox();
            _cmbFormat.Location = new Point(90, 25);
            _cmbFormat.Size = new Size(180, 23);
            _cmbFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbFormat.BackColor = Color.White;
            _cmbFormat.Items.AddRange(new object[]
            {
                "mp3（通用 192 kbps）", "m4a（原始音质直通）", "wav（无损，体积大）"
            });
            _cmbFormat.SelectedIndex = 0;

            Label l9 = MakeLabel("文件名：", 286, 28);
            l9.Size = new Size(58, 22);
            _txtName = new TextBox();
            _txtName.Location = new Point(350, 25);
            _txtName.Size = new Size(342, 23);
            _txtName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _txtName.TextChanged += OnNameChanged;

            _btnExport = MakeButton("③ 导出选区", 708, 24, 96, 28, AnchorStyles.Top | AnchorStyles.Right);
            _btnExport.Font = new Font(uiFont, FontStyle.Bold);
            _btnExport.Click += OnExport;

            Button btnOpenDir = MakeButton("打开文件夹", 812, 24, 108, 28, AnchorStyles.Top | AnchorStyles.Right);
            btnOpenDir.Click += OnOpenFolder;

            _lblExportStatus = new Label();
            _lblExportStatus.Text = "选区：0.0 ~ 0.0 秒";
            _lblExportStatus.Location = new Point(16, 62);
            _lblExportStatus.Size = new Size(896, 20);
            _lblExportStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lblExportStatus.Tag = "muted";
            _lblExportStatus.ForeColor = Color.FromArgb(90, 90, 100);

            g3.Controls.AddRange(new Control[] { l8, _cmbFormat, l9, _txtName, _btnExport, btnOpenDir, _lblExportStatus });

            // ---------- 日志 ----------
            _txtLog = new TextBox();
            _txtLog.Location = new Point(12, 588);
            _txtLog.Size = new Size(936, 80);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.WordWrap = false;
            _txtLog.BackColor = Color.White;
            _txtLog.Tag = "log";
            _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.AddRange(new Control[] { g1, g2, g3, _txtLog });
            ResumeLayout(false);
            PerformLayout();
        }

        private static GroupBox MakeGroup(string text, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.Location = new Point(x, y);
            g.Size = new Size(w, h);
            g.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            return g;
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.Size = new Size(74, 22);
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        private static Button MakeButton(string text, int x, int y, int w, int h, AnchorStyles anchor)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.Anchor = anchor;
            b.FlatStyle = FlatStyle.System;
            return b;
        }

        // ---------------- 组件准备 ----------------
        private void PrepareTools()
        {
            try
            {
                Tools.Ensure();
                _toolsReady = true;
                Post(delegate
                {
                    _lblDownStatus.Text = "组件就绪";
                    AppendLog("内置组件已就绪：" + Tools.Dir);
                    UpdateUiState();
                });
            }
            catch (Exception ex)
            {
                Post(delegate
                {
                    _lblDownStatus.Text = "组件准备失败";
                    AppendLog("组件准备失败：" + ex.Message);
                    if (!Silent)
                    {
                        MessageBox.Show(this, "内置组件解压失败：\n" + ex.Message, "错误",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            }
        }

        private string WorkDir
        {
            get
            {
                if (_workDir == null)
                {
                    _workDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MediaAudioExtractor", "work");
                }
                return _workDir;
            }
        }

        // ---------------- 第 1 步：下载完整音频 ----------------
        private void OnDownload(object sender, EventArgs e)
        {
            if (_busy) { return; }

            // 输入可以是网址，也可以是本地音频/视频文件（拖拽、粘贴进来的路径同样走这里）
            string raw = _txtUrl.Text.Trim();
            // UNC 路径要在这里就拦住：ResolveLocalFile 内部的 File.Exists 本身
            // 就会向对方主机发起 SMB 连接（可能泄露 NTLM 凭据）
            if (Extractor.IsUncPath(raw))
            {
                AppendLog("✘ 已拒绝网络路径（\\\\ 开头）：载入它会向该地址发起 SMB 连接并可能泄露本机凭据。");
                if (!Silent)
                {
                    MessageBox.Show(this,
                        "不支持网络路径（\\\\ 开头）。\n" +
                        "载入它会向该地址发起 SMB 连接并可能泄露本机凭据；\n" +
                        "请先把文件复制到本地磁盘再载入。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }
            string localPath = Extractor.ResolveLocalFile(raw);
            string url = null;
            if (localPath == null)
            {
                Match m = Regex.Match(raw, @"https?://[^\s]+");
                if (m.Success) { url = m.Value; }
            }
            if (localPath == null && url == null)
            {
                if (!Silent)
                {
                    MessageBox.Show(this,
                        "请输入视频网址（以 http:// 或 https:// 开头），\n或选择一个本地音频、视频文件。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }
            string outDir = _txtOut.Text.Trim();
            if (outDir.Length == 0)
            {
                if (!Silent) { MessageBox.Show(this, "请选择保存位置。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                return;
            }
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex)
            {
                _lblDownStatus.Text = "无法使用该保存位置";
                if (!Silent)
                {
                    MessageBox.Show(this, "无法使用该保存位置：\n" + ex.Message, "提示",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            string quality = "best";
            if (_cmbQuality.SelectedIndex == 1) { quality = "std"; }
            else if (_cmbQuality.SelectedIndex == 2) { quality = "low"; }

            // 必须真正 Close 而不能只 Stop：MCI 打开 preview.wav 后会一直持有该文件句柄，
            // 不释放的话下一次载入时 ffmpeg 无法覆盖它（Permission denied → 退出码 -13）。
            StopPlayback();
            try { _player.Close(); } catch (Exception) { }
            _session = null;
            _playerReady = false;
            _wave.HasAudio = false;
            _wave.Peaks = null;
            _wave.Invalidate();
            _cancel = false;
            _busy = true;
            _barDown.Marquee = false;
            _barDown.Value = 0;
            UpdateUiState();
            AppendLog(localPath != null ? "=== 载入本地文件 ===" : "=== 开始下载完整音频 ===");
            if (localPath != null)
            {
                AppendLog("文件：" + localPath);
                AppendLog("保存位置（仅作第 3 步导出目录）：" + outDir);
            }
            else
            {
                AppendLog("网址：" + url);
                AppendLog("音质：" + _cmbQuality.Text + "    保存到：" + outDir);
            }

            string[] p = new string[] { url, localPath, outDir, quality };
            Thread t = new Thread(new ThreadStart(delegate { JobDownload(p[0], p[1], p[2], p[3]); }));
            t.IsBackground = true;
            t.Start();
        }

        private void JobDownload(string url, string localPath, string outDir, string quality)
        {
            Extractor ex = new Extractor();
            ex.Log += delegate(string s) { LogLine(s); };
            ex.Status += delegate(string s) { Post(delegate { _lblDownStatus.Text = s; }); };
            ex.Progress += delegate(int v) { Post(delegate { SetProgress(_barDown, v); }); };
            _cancelEngine = ex;
            try
            {
                bool local = localPath != null;
                // 解码过程中回调：先把波形区摆好，再随进度增量补画
                Action<double, float[], double> onProgress = delegate(double dur, float[] partial, double frac)
                {
                    if (partial == null) { Post(delegate { BeginProgressiveWave(dur); }); }
                    else { Post(delegate { UpdatePartialPeaks(partial, frac); }); }
                };
                AudioSession s = local
                    ? ex.LoadLocal(localPath, WorkDir, onProgress)
                    : ex.DownloadWhole(url, outDir, quality, WorkDir, onProgress);
                Post(delegate { FinishDownload(s, s.Peaks, local); });
            }
            catch (Exception err)
            {
                bool cancelled = _cancel;
                Post(delegate
                {
                    _busy = false;
                    _barDown.Marquee = false;
                    // BeginProgressiveWave 在解码开始时就把 HasAudio / Peaks 摆好了；
                    // 载入没成功就必须复位，否则界面会为「没载入成的文件」画出半截波形
                    _wave.HasAudio = false;
                    _wave.Peaks = null;
                    _wave.DecodedFraction = 1;
                    _wave.Invalidate();
                    UpdateUiState();
                    if (cancelled)
                    {
                        _lblDownStatus.Text = "已取消";
                        AppendLog("下载已取消。");
                    }
                    else
                    {
                        _lblDownStatus.Text = "下载失败";
                        AppendLog("✘ 下载失败：" + err.Message);
                        if (!Silent)
                        {
                            MessageBox.Show(this, err.Message, "下载失败",
                                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                });
            }
            finally
            {
                _cancelEngine = null;
            }
        }

        /// <summary>
        /// 解码刚开始时调用：先把波形区按总时长摆好（含默认选区），
        /// 之后就能边解码边把波形从左往右画出来。
        /// 此时 _session 仍为 null，所以界面整体保持"忙碌"状态，不会误操作。
        /// </summary>
        private void BeginProgressiveWave(double dur)
        {
            if (dur <= 0.05) { return; }
            _wave.ResetView(dur);
            _wave.Peaks = new float[Extractor.PeakBuckets];
            _wave.DecodedFraction = 0;
            _wave.HasAudio = true;
            _wave.Position = 0;

            _syncing = true;
            decimal maxSec = (decimal)(Math.Floor(Math.Max(0.1, dur) * 10.0) / 10.0);
            if (maxSec < 0.1m) { maxSec = 0.1m; }
            _numStart.Maximum = maxSec;
            _numEnd.Maximum = maxSec;
            // 默认选区取整段时长的中间三分之一（与完成后的规则一致）
            double selA = dur / 3.0;
            double selB = dur * 2.0 / 3.0;
            if (selB - selA < 0.1) { selA = 0; selB = dur; }
            _wave.SelStart = selA;
            _wave.SelEnd = selB;
            SetNumValue(_numStart, selA);
            SetNumValue(_numEnd, selB);
            _syncing = false;

            _wave.Invalidate();
            UpdateSelectionLabels();
            UpdateTimeLabel();
        }

        /// <summary>解码进行中：把已解码部分的包络与进度刷到界面。</summary>
        private void UpdatePartialPeaks(float[] peaks, double fraction)
        {
            if (peaks == null) { return; }
            _wave.Peaks = peaks;
            _wave.DecodedFraction = fraction;
            _wave.Invalidate();

            int pct = (int)Math.Round(fraction * 100.0);
            if (pct < 0) { pct = 0; }
            if (pct > 100) { pct = 100; }
            SetProgress(_barDown, pct);
            _lblDownStatus.Text = "正在解码并绘制波形 " + pct + "%";
        }

        private void FinishDownload(AudioSession s, float[] peaks, bool local)
        {
            _session = s;
            _barDown.Marquee = false;
            _barDown.Value = 100;
            _lblDownStatus.Text = local ? "载入完成" : "下载完成";

            string error = _player.Open(s.PlaybackPath, s.Duration);
            _playerReady = (error == null);
            _playerError = error;
            if (error != null)
            {
                AppendLog("⚠ 试听不可用：" + error);
                _lblDownStatus.Text = local ? "已载入（试听不可用）" : "已下载（试听不可用）";
            }

            // 解码时已顺带算好包络；万一没有（旧路径）则补算一次
            if (peaks == null || peaks.Length == 0)
            {
                double sec;
                peaks = Extractor.ReadPeaksFromWav(s.PreviewWav, Extractor.PeakBuckets, out sec);
            }
            _fullPeaks = peaks;
            _wave.Peaks = peaks;
            _wave.DecodedFraction = 1;
            _wave.ResetView(s.Duration);
            _wave.HasAudio = true;
            _wave.Position = 0;
            _syncing = true;
            // Maximum 向下取整到 0.1 秒：确保任何「一位小数」的取值都不会超过实际时长，
            // 避免 NumericUpDown.Value 因越界抛异常（详见 SetNumValue 注释）
            decimal maxSec = (decimal)(Math.Floor(Math.Max(0.1, s.Duration) * 10.0) / 10.0);
            if (maxSec < 0.1m) { maxSec = 0.1m; }
            _numStart.Maximum = maxSec;
            _numEnd.Maximum = maxSec;
            // 默认选区取「整段时长的中间三分之一」，以此为起点向两侧微调更顺手
            double selA = s.Duration / 3.0;
            double selB = s.Duration * 2.0 / 3.0;
            if (selB - selA < 0.1) { selA = 0; selB = s.Duration; }   // 极短音频退化为整段
            _wave.SelStart = selA;
            _wave.SelEnd = selB;
            SetNumValue(_numStart, selA);
            SetNumValue(_numEnd, selB);
            _syncing = false;
            _nameDirty = false;
            UpdateDefaultName();
            _wave.Invalidate();
            UpdateSelectionLabels();
            UpdateTimeLabel();

            _lblExportStatus.Text = (local ? "本地文件时长 " : "源文件时长 ") + Fmt.Clock(s.Duration) +
                                    "，格式 " + s.FormatNote + "。拖动波形上的黄色操作杆选择要裁剪的片段。";
            AppendLog("✔ 试听文件就绪，可播放并拖动选区。");
            _busy = false;
            UpdateUiState();
        }

        // ---------------- 播放控制 ----------------
        private void OnPlayPause(object sender, EventArgs e)
        {
            if (!_player.IsOpen) { return; }
            if (_player.IsPlaying)
            {
                _player.Pause();
            }
            else
            {
                double pos = _wave.Position * 1000.0;
                if (pos >= _player.LengthMs - 50) { pos = 0; }
                _player.PlayFrom(pos);
                // 打开音频设备失败时不再静默（例如没有声卡、设备被独占）
                string err = _player.LastError;
                if (!string.IsNullOrEmpty(err)) { _lblDownStatus.Text = "⚠ " + err; }
                _ticker.Start();
            }
            UpdateTransportUi();
        }

        private void OnStop(object sender, EventArgs e)
        {
            StopPlayback();
            _wave.Position = 0;
            _wave.Invalidate();
            UpdateTimeLabel();
        }

        private void OnAudition(object sender, EventArgs e)
        {
            if (!_player.IsOpen) { return; }
            _player.PlayRange(_wave.SelStart * 1000.0, _wave.SelEnd * 1000.0);
            string err = _player.LastError;
            if (!string.IsNullOrEmpty(err)) { _lblDownStatus.Text = "⚠ " + err; }
            _ticker.Start();
            UpdateTransportUi();
        }

        private void OnWaveSeek(object sender, EventArgs e)
        {
            if (!_player.IsOpen) { return; }
            _player.Seek(_wave.Position * 1000.0);
            UpdateTimeLabel();
        }

        private void StopPlayback()
        {
            _ticker.Stop();
            try { _player.Stop(); } catch (Exception) { }
            UpdateTransportUi();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_player.IsOpen) { _ticker.Stop(); return; }
            if (!_player.IsPlaying)
            {
                _ticker.Stop();
                UpdateTransportUi();
                return;
            }
            _wave.Position = _player.PositionMs / 1000.0;
            if (_wave.Position > _wave.Duration) { _wave.Position = _wave.Duration; }
            _wave.Invalidate();
            UpdateTimeLabel();
        }

        private void UpdateTransportUi()
        {
            _btnPlay.Text = _player.IsPlaying ? "⏸ 暂停" : "▶ 播放";
        }

        private void UpdateTimeLabel()
        {
            _lblTime.Text = Fmt.Clock(_wave.Position) + " / " + Fmt.Clock(_wave.Duration);
        }

        // ---------------- 选区 ----------------

        /// <summary>
        /// 安全地给 NumericUpDown 赋值。
        /// NumericUpDown.Value 越界会抛 ArgumentOutOfRangeException（**不会**自动钳位到边界），
        /// 而 Maximum 是未取整的真实时长、赋值前又做了 Math.Round(...,1) 取整，
        /// 两者相差一个进位就会让「把选区拖到音频末尾」直接崩溃（如时长 1865.97 秒 → 取整得 1866.0）。
        /// 故所有赋值统一走这里，先取整再钳位到 [Minimum, Maximum]。
        /// </summary>
        internal static void SetNumValue(NumericUpDown nu, double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) { seconds = 0; }
            decimal v = (decimal)Math.Round(seconds, 1);
            if (v < nu.Minimum) { v = nu.Minimum; }
            if (v > nu.Maximum) { v = nu.Maximum; }
            nu.Value = v;
        }

        private void OnWaveSelection(object sender, EventArgs e)
        {
            _syncing = true;
            SetNumValue(_numStart, _wave.SelStart);
            SetNumValue(_numEnd, _wave.SelEnd);
            _syncing = false;
            UpdateSelectionLabels();
        }

        private void OnSelectionNumbers(object sender, EventArgs e)
        {
            if (_syncing || !_wave.HasAudio) { return; }
            double a = (double)_numStart.Value;
            double b = (double)_numEnd.Value;
            if (a > b) { b = a + 0.1; }
            double dur = _wave.Duration;
            if (b > dur) { b = dur; }
            if (a < 0) { a = 0; }
            if (a > b) { a = b; }
            _syncing = true;
            SetNumValue(_numStart, a);
            SetNumValue(_numEnd, b);
            _syncing = false;
            a = (double)_numStart.Value;
            b = (double)_numEnd.Value;
            _wave.SelStart = a;
            _wave.SelEnd = b;
            _wave.Invalidate();
            UpdateSelectionLabels();
        }

        private void OnMarkStart(object sender, EventArgs e)
        {
            if (!_wave.HasAudio) { return; }
            double p = _wave.Position;
            if (p > _wave.SelEnd - 0.05) { p = Math.Max(0, _wave.SelEnd - 0.05); }
            _wave.SelStart = p;
            _wave.Invalidate();
            OnWaveSelection(null, EventArgs.Empty);
        }

        private void OnMarkEnd(object sender, EventArgs e)
        {
            if (!_wave.HasAudio) { return; }
            double p = _wave.Position;
            if (p < _wave.SelStart + 0.05) { p = Math.Min(_wave.Duration, _wave.SelStart + 0.05); }
            _wave.SelEnd = p;
            _wave.Invalidate();
            OnWaveSelection(null, EventArgs.Empty);
        }

        private void OnSelectAll(object sender, EventArgs e)
        {
            if (!_wave.HasAudio) { return; }
            _wave.SelStart = 0;
            _wave.SelEnd = _wave.Duration;
            _wave.Invalidate();
            OnWaveSelection(null, EventArgs.Empty);
        }

        private void OnGoStart(object sender, EventArgs e)
        {
            StopPlayback();
            _wave.Position = 0;
            if (_player.IsOpen) { _player.Seek(0); }
            _wave.Invalidate();
            UpdateTimeLabel();
        }

        // ---------------- 缩放 / 平移 ----------------
        private void OnZoomIn(object sender, EventArgs e)
        {
            _wave.ZoomAt(1.6, (_wave.ViewStart + _wave.ViewEnd) / 2.0);
        }

        private void OnZoomOut(object sender, EventArgs e)
        {
            _wave.ZoomAt(1.0 / 1.6, (_wave.ViewStart + _wave.ViewEnd) / 2.0);
        }

        private void OnFitAll(object sender, EventArgs e)
        {
            _wave.FitAll();
        }

        private void OnFitSelection(object sender, EventArgs e)
        {
            _wave.FitSelection();
        }

        /// <summary>当前包络是否覆盖可视区间。</summary>
        private bool PeaksCoverView(double dur)
        {
            return _wave.PeakStart <= _wave.ViewStart + 0.001 &&
                   _wave.PeakEnd >= _wave.ViewEnd - 0.001;
        }

        /// <summary>退回整段包络（常驻内存，无需读盘）。</summary>
        private void UseFullPeaks(double dur)
        {
            _wave.Peaks = _fullPeaks;
            _wave.PeakStart = 0;
            _wave.PeakEnd = dur;
        }

        /// <summary>当前用的是否为整段包络。</summary>
        private bool IsFullPeaks(double dur)
        {
            return _wave.PeakStart <= 0.001 && _wave.PeakEnd >= dur - 0.001;
        }

        /// <summary>
        /// 视图变化后按可视区间重算高分辨率波形包络。
        ///
        /// ⚠ 平移时本方法会被**每个鼠标移动事件**调用，因此绝不能在这里无脑读盘：
        /// 跨度几十秒时一次重算要几十毫秒（实测 75 秒跨度约 94 ms/步），会直接拖垮拖动手感。
        /// 处理策略见下面四步。
        /// </summary>
        private void OnWaveViewChanged(object sender, EventArgs e)
        {
            if (_session == null || _fullPeaks == null) { return; }
            double dur = _session.Duration <= 0 ? 1 : _session.Duration;

            // 1) 交互进行中（右键平移 / 拖动操作杆）：一律不读盘。
            //    现有包络是按「绝对时间」映射到像素的，位置依然正确，只是分辨率可能偏低；
            //    若它已覆盖不到可视区间，就退回常驻内存的整段包络。
            if (_wave.IsInteracting)
            {
                if (!PeaksCoverView(dur)) { UseFullPeaks(dur); }
                _wave.Invalidate();
                return;
            }

            double span = _wave.ViewEnd - _wave.ViewStart;

            // 2) 整段（或跨度过大）：直接用常驻的整段包络，同样无需读盘
            if ((_wave.ViewStart <= 0.001 && _wave.ViewEnd >= dur - 0.001) || span > 120)
            {
                UseFullPeaks(dur);
                _wave.Invalidate();
                return;
            }

            // 3) 已缓存的区间包络既覆盖当前视图、分辨率也够用 → 命中缓存，不重算。
            //    只看「覆盖」是不够的：缩放进来的视图虽然落在缓存区间内，但可用的桶数
            //    会随之变少，必须同时校验「可视区间内至少有约一桶一像素」。
            if (!IsFullPeaks(dur) && PeaksCoverView(dur))
            {
                double cachedSpan = Math.Max(0.001, _wave.PeakEnd - _wave.PeakStart);
                int have = _wave.Peaks == null ? 0 : _wave.Peaks.Length;
                int need = Math.Max(400, _wave.ClientSize.Width);
                double usable = have * (span / cachedSpan);
                if (usable >= need * 0.9)
                {
                    _wave.Invalidate();
                    return;
                }
            }

            // 4) 重算：前后各留 50% 余量，使小幅平移 / 缩放能命中缓存（第 3 步）
            double margin = span * 0.5;
            double a = Math.Max(0, _wave.ViewStart - margin);
            double b = Math.Min(dur, _wave.ViewEnd + margin);
            int width = Math.Max(400, _wave.ClientSize.Width);
            int buckets = (int)Math.Round(width * ((b - a) / Math.Max(0.001, span)));
            if (buckets < 400) { buckets = 400; }
            if (buckets > 12000) { buckets = 12000; }
            double total;
            _peakReloadCount++;
            _wave.Peaks = Extractor.ReadPeaksRange(_session.PreviewWav, a, b, buckets, out total);
            _wave.PeakStart = a;
            _wave.PeakEnd = b;
            _wave.Invalidate();
        }

        private void UpdateSelectionLabels()
        {
            double len = _wave.SelEnd - _wave.SelStart;
            if (len < 0) { len = 0; }
            _lblSelLen.Text = "选区时长：" + Fmt.Num(len) + " 秒";
            _lblExportStatus.Text = "选区 " + Fmt.Clock(_wave.SelStart) + " ~ " + Fmt.Clock(_wave.SelEnd) +
                                    "（" + Fmt.Num(len) + " 秒）" +
                                    (_session != null ? "    源：" + _session.Title : "");
            UpdateDefaultName();
        }

        private void UpdateDefaultName()
        {
            if (_nameDirty) { return; }
            if (_session == null) { return; }
            string suffix;
            if (_wave.SelStart <= 0.001 && Math.Abs(_wave.SelEnd - _wave.Duration) < 0.05)
            {
                suffix = "_完整音频";
            }
            else
            {
                suffix = "_" + Fmt.Num(Math.Round(_wave.SelStart, 1)) + "s-" + Fmt.Num(Math.Round(_wave.SelEnd, 1)) + "s";
            }
            _settingName = true;
            _txtName.Text = Extractor.SanitizeName(_session.Title) + suffix;
            _settingName = false;
        }

        private bool _settingName;

        private void OnNameChanged(object sender, EventArgs e)
        {
            if (!_settingName) { _nameDirty = true; }
        }

        // ---------------- 第 3 步：导出 ----------------
        private void OnExport(object sender, EventArgs e)
        {
            if (_busy || _session == null) { return; }
            double s = _wave.SelStart;
            double en = _wave.SelEnd;
            if (en - s < 0.05)
            {
                if (!Silent)
                {
                    MessageBox.Show(this, "选区太短，请拖动操作杆选择更长的片段。", "提示",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }
            string outDir = _txtOut.Text.Trim();
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex)
            {
                if (!Silent) { MessageBox.Show(this, "无法写入该目录：\n" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                return;
            }

            string format = "mp3";
            if (_cmbFormat.SelectedIndex == 1) { format = "m4a"; }
            else if (_cmbFormat.SelectedIndex == 2) { format = "wav"; }

            string name = _txtName.Text.Trim();
            if (name.Length == 0) { name = "audio"; }

            StopPlayback();
            _cancel = false;
            _busy = true;
            UpdateUiState();
            AppendLog("=== 导出选区 " + Fmt.Num(s) + "s ~ " + Fmt.Num(en) + "s ，格式 " + format + " ===");

            string src = _session.SourcePath;
            string wd = WorkDir;
            Thread t = new Thread(new ThreadStart(delegate { JobExport(src, s, en - s, format, outDir, name, wd); }));
            t.IsBackground = true;
            t.Start();
        }

        private void JobExport(string src, double start, double dur, string format,
                               string outDir, string name, string workDir)
        {
            Extractor ex = new Extractor();
            ex.Status += delegate(string s2) { Post(delegate { _lblExportStatus.Text = s2; }); };
            ex.Log += delegate(string s2) { LogLine(s2); };
            _cancelEngine = ex;
            try
            {
                string path = ex.CutLocalFile(src, start, dur, format, outDir, name, workDir);
                double real = Extractor.ProbeDuration(path);
                Post(delegate
                {
                    _busy = false;
                    _lastFile = path;
                    UpdateUiState();
                    _lblExportStatus.Text = "✔ 已导出：" + path + "（" + real.ToString("0.00") + " 秒）";
                    AppendLog("✔ 导出完成，输出时长 " + real.ToString("0.00") + " 秒");
                });
            }
            catch (Exception err)
            {
                bool cancelled = _cancel;
                Post(delegate
                {
                    _busy = false;
                    UpdateUiState();
                    if (cancelled)
                    {
                        _lblExportStatus.Text = "已取消";
                        AppendLog("导出已取消。");
                    }
                    else
                    {
                        _lblExportStatus.Text = "导出失败：" + err.Message;
                        AppendLog("✘ 导出失败：" + err.Message);
                        if (!Silent)
                        {
                            MessageBox.Show(this, err.Message, "导出失败",
                                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                });
            }
            finally
            {
                _cancelEngine = null;
            }
        }

        private Extractor _cancelEngine;

        // ---------------- 其它界面事件 ----------------
        private void OnPaste(object sender, EventArgs e)
        {
            try
            {
                // 资源管理器「复制」文件 → 剪贴板里是文件列表，取第一个
                if (Clipboard.ContainsFileDropList())
                {
                    System.Collections.Specialized.StringCollection files = Clipboard.GetFileDropList();
                    if (files != null && files.Count > 0)
                    {
                        _txtUrl.Text = files[0];
                        return;
                    }
                }
                if (Clipboard.ContainsText())
                {
                    string t = Clipboard.GetText();
                    Match m = Regex.Match(t, @"https?://[^\s]+");
                    // 没有链接就按本地路径处理（「复制为路径」会带引号，交给 ResolveLocalFile 去引号）
                    _txtUrl.Text = m.Success ? m.Value : t.Trim().Trim('"').Trim();
                }
            }
            catch (Exception) { }
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "请选择保存位置";
                d.ShowNewFolderButton = true;
                if (Directory.Exists(_txtOut.Text)) { d.SelectedPath = _txtOut.Text; }
                if (d.ShowDialog(this) == DialogResult.OK) { _txtOut.Text = d.SelectedPath; }
            }
        }

        private void OnOpenFolder(object sender, EventArgs e)
        {
            string path = _lastFile;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                path = _txtOut.Text;
                if (!Directory.Exists(path)) { return; }
                Process.Start("explorer.exe", "\"" + path + "\"");
                return;
            }
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }

        private void OnCancel(object sender, EventArgs e)
        {
            _cancel = true;
            _lblDownStatus.Text = "正在取消 …";
            Extractor ex = _cancelEngine;
            if (ex != null) { ex.Cancel(); }
        }

        /// <summary>当前输入是否为本地文件。</summary>
        private bool InputIsLocal()
        {
            return Extractor.ResolveLocalFile(_txtUrl.Text) != null;
        }

        /// <summary>
        /// 输入是本地文件时：按钮改为「载入本地文件」，并停用「音质」（对本地文件无意义）。
        /// </summary>
        private void UpdateSourceMode()
        {
            bool local = InputIsLocal();
            string want = local ? "① 载入本地文件" : "① 下载完整音频";
            if (_btnDownload.Text != want) { _btnDownload.Text = want; }
            if (!_busy) { _cmbQuality.Enabled = !local; }
        }

        private void UpdateUiState()
        {
            bool has = _session != null && _wave.HasAudio;
            bool idle = !_busy;
            bool tools = _toolsReady;
            bool play = has && _playerReady;
            bool local = InputIsLocal();

            _txtUrl.Enabled = idle;
            _txtOut.Enabled = idle;
            _btnPaste.Enabled = idle;
            _btnBrowse.Enabled = idle;
            _cmbQuality.Enabled = idle && !local;
            _btnDownload.Enabled = idle && tools;
            _btnCancel.Enabled = !idle;

            _wave.Enabled = has;
            _btnPlay.Enabled = play && idle;
            _btnStop.Enabled = play;
            _btnAudition.Enabled = play && idle;
            _btnMarkStart.Enabled = has && idle;
            _btnMarkEnd.Enabled = has && idle;
            _btnSelectAll.Enabled = has && idle;
            _numStart.Enabled = has && idle;
            _numEnd.Enabled = has && idle;

            _cmbFormat.Enabled = has && idle;
            _txtName.Enabled = has && idle;
            _btnExport.Enabled = has && idle;
        }

        private volatile bool _toolsReady;
        private bool _playerReady;

        private void SetProgress(FlatProgress bar, int pct)
        {
            if (pct < 0)
            {
                bar.Marquee = true;
                return;
            }
            bar.Marquee = false;
            bar.Value = pct;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy && !Silent)
            {
                DialogResult r = MessageBox.Show(this, "任务正在进行，确定要取消并退出吗？",
                                                 "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                _cancel = true;
                Extractor ex = _cancelEngine;
                if (ex != null) { ex.Cancel(); }
            }
            _ticker.Stop();
            try { _player.Close(); } catch (Exception) { }
            base.OnFormClosing(e);
        }

        // ---------------- 主题（跟随系统浅色 / 深色） ----------------
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private Theme _theme;

        internal void ApplyTheme(Theme t)
        {
            _theme = t;
            t.Apply(this);
            ApplyTitleBarTheme();
            Invalidate(true);
        }

        private void ApplyTitleBarTheme()
        {
            if (!IsHandleCreated) { return; }
            try
            {
                int v = (_theme != null && _theme.Dark) ? 1 : 0;
                if (DwmSetWindowAttribute(Handle, 20, ref v, 4) != 0)
                {
                    DwmSetWindowAttribute(Handle, 19, ref v, 4);   // Win10 1809
                }
            }
            catch (Exception) { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBarTheme();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x001A)      // WM_SETTINGCHANGE：系统主题变化
            {
                string k = null;
                try { k = Marshal.PtrToStringUni(m.LParam); }
                catch (Exception) { }
                if (string.IsNullOrEmpty(k) || k == "ImmersiveColorSet" || k == "WindowMetrics")
                {
                    Theme t = Theme.Current();
                    if (_theme == null || t.Dark != _theme.Dark) { ApplyTheme(t); }
                }
            }
        }

        // ---------------- 日志与线程调度 ----------------
        private void Post(Action a)
        {
            if (IsDisposed) { return; }
            try { BeginInvoke(a); }
            catch (InvalidOperationException) { try { a(); } catch (Exception) { } }
        }

        private void LogLine(string s) { Post(delegate { AppendLog(s); }); }

        private void AppendLog(string s)
        {
            if (_txtLog.IsDisposed) { return; }
            _txtLog.AppendText(s + "\r\n");
            string[] lines = _txtLog.Lines;
            if (lines.Length > 400)
            {
                string[] keep = new string[200];
                Array.Copy(lines, lines.Length - 200, keep, 0, 200);
                _txtLog.Lines = keep;
            }
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }

        // ---------------- 自检接口（--dumpui / --uiauto 使用） ----------------
        internal bool Silent;
        internal bool ProbeBusy { get { return _busy; } }
        internal string ProbeLog { get { return _txtLog.Text; } }
        internal string ProbeDownStatus { get { return _lblDownStatus.Text; } }
        internal string ProbeExportStatus { get { return _lblExportStatus.Text; } }
        internal string ProbeOutFile { get { return _lastFile; } }
        internal double ProbeDuration { get { return _session == null ? 0 : _session.Duration; } }
        internal float[] ProbePeaks { get { return _wave.Peaks; } }
        internal double ProbeSelStart { get { return _wave.SelStart; } }
        internal double ProbeSelEnd { get { return _wave.SelEnd; } }
        internal double ProbePosition { get { return _wave.Position; } }
        internal Button ProbeDownloadButton { get { return _btnDownload; } }
        internal Button ProbeExportButton { get { return _btnExport; } }
        internal Button ProbePlayButton { get { return _btnPlay; } }
        internal Button ProbeAuditionButton { get { return _btnAudition; } }
        internal NumericUpDown ProbeStartNum { get { return _numStart; } }
        internal NumericUpDown ProbeEndNum { get { return _numEnd; } }
        internal string ProbePlayerError { get { return _playerError; } }
        internal string ProbeWavPath { get { return _session == null ? null : _session.PreviewWav; } }
        internal double ProbeViewStart { get { return _wave.ViewStart; } }
        internal double ProbeViewEnd { get { return _wave.ViewEnd; } }
        internal double ProbePeakStart { get { return _wave.PeakStart; } }
        internal double ProbePeakEnd { get { return _wave.PeakEnd; } }

        internal void ProbeZoom(double factor, double center) { _wave.ZoomAt(factor, center); }
        internal void ProbeFitSelection() { _wave.FitSelection(); }
        internal void ProbeFitAll() { _wave.FitAll(); }

        internal int ProbePeakReloadCount { get { return _peakReloadCount; } }
        internal void ProbeResetPeakReloadCount() { _peakReloadCount = 0; }
        // 尚未开始载入时（Peaks 为空）返回 -1，避免把字段默认值误当成真实进度
        internal double ProbeDecodedFraction { get { return _wave.Peaks == null ? -1 : _wave.DecodedFraction; } }

        /// <summary>自检用：来回平移视图，模拟右键拖动的热路径。</summary>
        internal void ProbePan(int steps, double dt)
        {
            for (int i = 0; i < steps; i++)
            {
                _wave.SimulatePanStep((i % 2 == 0) ? dt : -dt);
            }
        }

        private string _playerError;

        internal void ProbeFill(string url, string outDir, int qualityIndex)
        {
            if (!string.IsNullOrEmpty(url)) { _txtUrl.Text = url; }
            if (!string.IsNullOrEmpty(outDir)) { _txtOut.Text = outDir; }
            if (qualityIndex >= 0 && qualityIndex < _cmbQuality.Items.Count)
            {
                _cmbQuality.SelectedIndex = qualityIndex;
            }
        }

        internal void ProbeSetSelection(double a, double b)
        {
            _wave.SelStart = a;
            _wave.SelEnd = b;
            _wave.Invalidate();
            OnWaveSelection(null, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------
    //  界面自检（隐藏模式）：--dumpui [截图路径] / --uiauto <网址> <目录> [格式] [截图]
    // ------------------------------------------------------------------
    internal static class UiProbe
    {
        public static int Run(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string png = OptArg(args, 1);
            string themeArg = args.Length > 2 ? args[2] : null;
            if (themeArg == "dark") { Theme.Force = true; }
            else if (themeArg == "light") { Theme.Force = false; }
            StringBuilder report = new StringBuilder();
            report.AppendLine("系统主题: " + (Theme.SystemIsDark() ? "深色" : "浅色") +
                              (Theme.Force.HasValue ? "（自检强制）" : ""));

            MainForm f = new MainForm();
            f.Silent = true;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.ShowInTaskbar = false;
            f.Show();
            Pump(2000);
            Snapshot(f, "初始尺寸 " + f.ClientSize.Width + " x " + f.ClientSize.Height, report);

            if (png != null) { Save(f, png, report); }

            f.Size = new Size(1100, 800);
            Pump(500);
            Snapshot(f, "放大后 " + f.ClientSize.Width + " x " + f.ClientSize.Height, report);

            f.Size = f.MinimumSize;
            Pump(500);
            Snapshot(f, "最小尺寸 " + f.ClientSize.Width + " x " + f.ClientSize.Height, report);

            f.Close();
            Emit(report.ToString(), png);
            return 0;
        }

        // 端到端：下载完整音频 → 设置选区 → 导出 → 校验
        public static int RunAuto(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string url = args.Length > 1 ? args[1] : "";
            string outDir = args.Length > 2 ? args[2] : Environment.CurrentDirectory;
            int quality = args.Length > 3 ? ParseInt(args[3], 0) : 0;
            string png = OptArg(args, 4);
            string themeArg = args.Length > 5 ? args[5] : null;
            if (themeArg == "dark") { Theme.Force = true; }
            else if (themeArg == "light") { Theme.Force = false; }
            double selA = 0, selB = 0;      // 载入后按时长自适应，见「第 2 步」之前
            StringBuilder sb = new StringBuilder();

            MainForm f = new MainForm();
            f.Silent = true;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.ShowInTaskbar = false;
            f.Show();
            Pump(1500);

            f.ProbeFill(url, outDir, quality);
            Stopwatch boot = Stopwatch.StartNew();
            while (!f.ProbeDownloadButton.Enabled && boot.ElapsedMilliseconds < 60000)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
            sb.AppendLine("【第 1 步】点击下载完整音频（等待组件就绪 " +
                          (boot.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒）");

            Stopwatch sw = Stopwatch.StartNew();
            f.ProbeDownloadButton.PerformClick();
            // 载入过程中采样波形进度，验证「边解码边画」
            double firstFrac = -1, firstFracMs = -1, maxFrac = 0;
            bool midShot = false;
            int ticks = 0;
            while (f.ProbeBusy && sw.ElapsedMilliseconds < 600000)
            {
                Application.DoEvents();
                Thread.Sleep(40);
                ticks++;
                double fr = f.ProbeDecodedFraction;
                if (fr > 0.0001 && firstFrac < 0) { firstFrac = fr; firstFracMs = sw.ElapsedMilliseconds; }
                if (fr > maxFrac) { maxFrac = fr; }
                // 解码到约 35% 时留一张截图，作为「边解码边画」的视觉证据
                if (png != null && !midShot && fr >= 0.35)
                {
                    midShot = true;
                    Save(f, png + ".loading.png", new StringBuilder());
                }
            }
            Pump(600);
            sb.AppendLine("  耗时: " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒");
            sb.AppendLine("  渐进绘制: 首次出现波形 " +
                          (firstFracMs < 0 ? "未出现" : "@" + (firstFracMs / 1000.0).ToString("0.0") + " 秒") +
                          "，最终进度 " + (maxFrac * 100).ToString("0") + "%（轮询 " + ticks + " 次）");
            sb.AppendLine("  忙碌已复位: " + (!f.ProbeBusy));
            sb.AppendLine("  状态: " + f.ProbeDownStatus);
            sb.AppendLine("  音频总时长: " + f.ProbeDuration.ToString("0.00") + " 秒");
            sb.AppendLine("  试听文件: " + f.ProbeWavPath + "  存在=" +
                          (f.ProbeWavPath != null && File.Exists(f.ProbeWavPath)));
            sb.AppendLine("  波形峰值点数: " + (f.ProbePeaks == null ? 0 : f.ProbePeaks.Length));
            sb.AppendLine("  默认选区: " + f.ProbeSelStart.ToString("0.0") + " ~ " + f.ProbeSelEnd.ToString("0.0"));
            sb.AppendLine("  播放可用: " + f.ProbePlayButton.Enabled);

            // 播放测试
            if (f.ProbePlayButton.Enabled)
            {
                f.ProbePlayButton.PerformClick();
                Pump(1200);
                sb.AppendLine("  播放 1.2 秒后播放头位置: " + f.ProbePosition.ToString("0.00") + " 秒");
                sb.AppendLine("  播放按钮文本: " + f.ProbePlayButton.Text);
                f.ProbePlayButton.PerformClick();     // 暂停
                Pump(200);
            }

            // 选区按时长自适应（取中间三分之一，与程序默认一致）。
            // 以前硬编码 5~12 秒：对 ≤12 秒的音频会被钳成零长度，
            // 导出步骤必然失败，看起来像产品缺陷，其实是自检自己的问题。
            double adur = f.ProbeDuration;
            selA = Math.Round(adur / 3.0 * 10) / 10;
            selB = Math.Round(adur * 2.0 / 3.0 * 10) / 10;
            if (selB - selA < 0.2) { selA = 0; selB = Math.Min(adur, 1.0); }
            if (selB <= selA) { selB = selA + 0.1; }

            sb.AppendLine("【第 2 步】设置选区 " + selA + " ~ " + selB + " 秒");
            MainForm.SetNumValue(f.ProbeStartNum, selA);
            MainForm.SetNumValue(f.ProbeEndNum, selB);
            Pump(200);
            sb.AppendLine("  波形选区: " + f.ProbeSelStart.ToString("0.0") + " ~ " + f.ProbeSelEnd.ToString("0.0"));
            sb.AppendLine("  全览视图: " + f.ProbeViewStart.ToString("0.0") + " ~ " + f.ProbeViewEnd.ToString("0.0"));

            sb.AppendLine("【第 2b 步】缩放");
            f.ProbeZoom(1.6, 8.5);
            Pump(150);
            sb.AppendLine("  放大后视图: " + f.ProbeViewStart.ToString("0.0") + " ~ " + f.ProbeViewEnd.ToString("0.0") +
                          "   包络区间: " + f.ProbePeakStart.ToString("0.0") + " ~ " + f.ProbePeakEnd.ToString("0.0"));

            // 平移热路径：模拟右键拖动（每个鼠标事件都会走 SetView → ViewChanged → 重算包络）
            sb.AppendLine("【第 2c 步】模拟平移 60 步（右键拖动热路径）");
            double panSpan = f.ProbeViewEnd - f.ProbeViewStart;
            double panStep = panSpan / 40.0;
            f.ProbeResetPeakReloadCount();
            Stopwatch swPan = Stopwatch.StartNew();
            f.ProbePan(60, panStep);
            swPan.Stop();
            sb.AppendLine("  视图跨度: " + panSpan.ToString("0.0") + " 秒   每步平移: " + panStep.ToString("0.00") + " 秒");
            sb.AppendLine("  耗时: " + swPan.ElapsedMilliseconds + " ms（" +
                          (swPan.ElapsedMilliseconds / 60.0).ToString("0.00") + " ms/步）");
            sb.AppendLine("  包络重算（读盘）次数: " + f.ProbePeakReloadCount + " / 60 步");

            f.ProbeFitSelection();
            Pump(150);
            sb.AppendLine("  聚焦选区后视图: " + f.ProbeViewStart.ToString("0.0") + " ~ " + f.ProbeViewEnd.ToString("0.0") +
                          "   包络区间: " + f.ProbePeakStart.ToString("0.0") + " ~ " + f.ProbePeakEnd.ToString("0.0"));
            f.ProbeFitAll();
            Pump(150);
            sb.AppendLine("  恢复全览: " + f.ProbeViewStart.ToString("0.0") + " ~ " + f.ProbeViewEnd.ToString("0.0"));
            f.ProbeFitSelection();
            Pump(150);

            sb.AppendLine("【第 3 步】导出选区");
            sw = Stopwatch.StartNew();
            f.ProbeExportButton.PerformClick();
            while (f.ProbeBusy && sw.ElapsedMilliseconds < 300000) { Application.DoEvents(); Thread.Sleep(40); }
            Pump(600);
            sb.AppendLine("  耗时: " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒");
            sb.AppendLine("  状态: " + f.ProbeExportStatus);
            string file = f.ProbeOutFile;
            sb.AppendLine("  结果文件: " + (file == null ? "<无>" : file));
            bool exists = file != null && File.Exists(file);
            sb.AppendLine("  文件是否存在: " + exists);
            if (exists)
            {
                double d = Extractor.ProbeDuration(file);
                sb.AppendLine("  输出时长: " + d.ToString("0.00") + " 秒（期望 " + (selB - selA).ToString("0.0") + " 秒）");
            }

            // 二次载入回归：验证「上一份试听 WAV 仍被 MCI 播放器占用」时，再次载入不会失败
            string second = args.Length > 6 ? args[6] : null;
            bool ok2 = true;
            if (second != null)
            {
                sb.AppendLine("【第 4 步】二次载入（回归：旧试听文件被占用时不应失败）");
                sb.AppendLine("  来源: " + second);
                f.ProbeFill(second, outDir, quality);
                Stopwatch sw2 = Stopwatch.StartNew();
                f.ProbeDownloadButton.PerformClick();
                while (f.ProbeBusy && sw2.ElapsedMilliseconds < 300000) { Application.DoEvents(); Thread.Sleep(40); }
                Pump(600);
                sb.AppendLine("  耗时: " + (sw2.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒");
                sb.AppendLine("  状态: " + f.ProbeDownStatus);
                sb.AppendLine("  音频总时长: " + f.ProbeDuration.ToString("0.00") + " 秒");
                sb.AppendLine("  试听可用: " + f.ProbePlayButton.Enabled);
                ok2 = f.ProbeDuration > 0.05;
                sb.AppendLine("  二次载入: " + (ok2 ? "通过" : "失败"));
            }

            sb.AppendLine("--- 日志 ---");
            sb.AppendLine(f.ProbeLog);

            if (png != null) { Save(f, png, report: sb); }
            f.Close();
            Emit(sb.ToString(), png);
            return (exists && ok2) ? 0 : 1;
        }

        // 运行时主题切换自检：模拟系统广播 WM_SETTINGCHANGE
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static int RunThemeTest(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string png = OptArg(args, 1);
            string which = args.Length > 2 ? args[2] : null;
            if (which == "light") { Theme.Force = false; }
            if (which == "dark") { Theme.Force = true; }

            StringBuilder sb = new StringBuilder();
            MainForm f = new MainForm();
            f.Silent = true;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.ShowInTaskbar = false;
            f.Show();
            Pump(1200);

            bool darkBefore = Theme.SystemIsDark();
            sb.AppendLine("初始主题: " + (darkBefore ? "深色" : "浅色"));
            sb.AppendLine("  窗体背景 = " + C(f.BackColor));
            Control log = FindTagged(f, "log");
            sb.AppendLine("  日志框背景 = " + (log == null ? "?" : C(log.BackColor)));

            // 翻转并广播，模拟用户在系统设置里切换主题
            Theme.Force = !darkBefore;
            IntPtr lp = Marshal.StringToHGlobalUni("ImmersiveColorSet");
            try { SendMessage(f.Handle, 0x001A, IntPtr.Zero, lp); }
            finally { Marshal.FreeHGlobal(lp); }
            Pump(600);

            bool darkAfter = Theme.SystemIsDark();
            sb.AppendLine("广播后主题: " + (darkAfter ? "深色" : "浅色"));
            sb.AppendLine("  窗体背景 = " + C(f.BackColor));
            sb.AppendLine("  日志框背景 = " + (log == null ? "?" : C(log.BackColor)));
            bool switched = darkBefore != darkAfter && log != null && log.BackColor != Color.White == darkAfter;
            sb.AppendLine("运行时切换生效: " + (switched ? "是" : "否"));

            if (png != null) { Save(f, png, sb); }
            f.Close();
            Emit(sb.ToString(), png);
            return switched ? 0 : 1;
        }

        private static string C(Color c) { return c.R + "," + c.G + "," + c.B; }

        private static Control FindTagged(Control root, string tag)
        {
            if (Convert.ToString(root.Tag) == tag) { return root; }
            foreach (Control c in root.Controls)
            {
                Control r = FindTagged(c, tag);
                if (r != null) { return r; }
            }
            return null;
        }

        private static int ParseInt(string s, int def)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static void Save(Form f, string png, StringBuilder report)
        {
            try
            {
                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                }
                if (report != null) { report.AppendLine("截图已保存: " + png); }
            }
            catch (Exception e) { if (report != null) { report.AppendLine("截图失败: " + e.Message); } }
        }

        /// <summary>
        /// 取可选路径参数：空串/空白按「未提供」处理。
        /// 否则 `--uiauto ... "" ""` 这种调用会写出 ".txt" 这类怪文件。
        /// </summary>
        private static string OptArg(string[] args, int index)
        {
            if (args.Length <= index) { return null; }
            string s = args[index];
            if (s == null) { return null; }
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        private static void Emit(string text, string png)
        {
            try
            {
                string path = string.IsNullOrEmpty(png) ? "ui.txt" : png + ".txt";
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception) { }
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception) { }
            Console.Write(text);
        }

        private static void Pump(int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
        }

        private static void Snapshot(Form f, string title, StringBuilder sb)
        {
            float dpi = 96f;
            try { using (Graphics g = f.CreateGraphics()) { dpi = g.DpiX; } }
            catch (Exception) { }
            sb.AppendLine("=== " + title + " ===  font=" + f.Font.Name + "  dpi=" + dpi);
            int bad = 0;
            foreach (Control top in f.Controls)
            {
                Rectangle client = f.ClientRectangle;
                Rectangle r = top.Bounds;
                bool inside = r.Left >= 0 && r.Top >= 0 && r.Right <= client.Width && r.Bottom <= client.Height;
                if (!inside) { bad++; }
                string text = top.Text == null ? "" : top.Text.Replace("\r", " ").Replace("\n", " ");
                if (text.Length > 26) { text = text.Substring(0, 26) + "…"; }
                sb.AppendFormat("{0,-11} {1,-29} [{2,4},{3,4},{4,4},{5,4}] enabled={6,-5} inside={7}",
                    top.GetType().Name, text, r.X, r.Y, r.Width, r.Height, top.Enabled, inside ? "yes" : "NO");
                sb.AppendLine();

                GroupBox gb = top as GroupBox;
                if (gb == null) { continue; }
                foreach (Control c in gb.Controls)
                {
                    Rectangle cr = c.Bounds;
                    bool ok = cr.Left >= 0 && cr.Top >= 0 &&
                              cr.Right <= gb.ClientSize.Width && cr.Bottom <= gb.ClientSize.Height;
                    if (!ok) { bad++; }
                    string t2 = c.Text == null ? "" : c.Text.Replace("\r", " ").Replace("\n", " ");
                    if (t2.Length > 26) { t2 = t2.Substring(0, 26) + "…"; }
                    sb.AppendFormat("   └ {0,-9} {1,-29} [{2,4},{3,4},{4,4},{5,4}] enabled={6,-5} inside={7}",
                        c.GetType().Name, t2, cr.X, cr.Y, cr.Width, cr.Height, c.Enabled, ok ? "yes" : "NO");
                    sb.AppendLine();
                }
                Control[] kids = new Control[gb.Controls.Count];
                gb.Controls.CopyTo(kids, 0);
                for (int i = 0; i < kids.Length; i++)
                {
                    for (int j = i + 1; j < kids.Length; j++)
                    {
                        if (kids[i] is Label && kids[j] is Label) { continue; }
                        if (kids[i].Bounds.IntersectsWith(kids[j].Bounds))
                        {
                            bad++;
                            sb.AppendLine("   !! 重叠(" + top.Text + "): " + kids[i].Text + " <-> " + kids[j].Text);
                        }
                    }
                }
            }
            sb.AppendLine(bad == 0 ? "检查结果: 全部通过（无越界）" : ("检查结果: 发现 " + bad + " 处越界"));
            sb.AppendLine();
        }
    }

    // ------------------------------------------------------------------
    //  命令行模式
    // ------------------------------------------------------------------
    internal static class Cli
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleOutputCP(uint wCodePageID);

        public static int Run(string[] args)
        {
            try
            {
                AttachConsole(-1);
                SetConsoleOutputCP(65001);
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception) { }

            Dictionary<string, string> o = ParseArgs(args);

            if (o.ContainsKey("peaks")) { return DoPeaks(o); }
            if (o.ContainsKey("preview")) { return DoPreview(o); }
            if (o.ContainsKey("cut")) { return DoCut(o); }
            if (o.ContainsKey("full")) { return DoFull(o); }
            if (o.ContainsKey("url")) { return DoOneShot(o); }

            Console.WriteLine("视频音频提取器 v" + Program.AppVersion + " — 命令行模式");
            Console.WriteLine("  --cli --url <网址> --out <目录> [--start 0] [--dur 15] [--format mp3] [--no-section]");
            Console.WriteLine("  --cli --full --url <网址> --out <目录> [--quality best|std|low]");
            Console.WriteLine("  --cli --cut <本地文件> --out <目录> [--start 0] [--end 15] [--format mp3] [--name 名称]");
            Console.WriteLine("  --cli --preview <本地文件> --wav <输出.wav> [--rate 44100]");
            Console.WriteLine("  --cli --peaks <wav> [--buckets 1000]");
            return 2;
        }

        // ---- 下载完整音频 ----
        private static int DoFull(Dictionary<string, string> o)
        {
            string url = Get(o, "url");
            string outDir = Get(o, "out");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(outDir))
            {
                Console.WriteLine("错误: --full 需要 --url 与 --out");
                return 2;
            }
            string work = WorkDir();
            try
            {
                Directory.CreateDirectory(outDir);
                Extractor ex = MakeEngine();
                AudioSession s = ex.DownloadWhole(url, outDir, Get(o, "quality"), work);
                Console.WriteLine("标题: " + s.Title);
                Console.WriteLine("完整时长: " + s.Duration.ToString("0.00") + " 秒");
                Console.WriteLine("源文件: " + s.SourcePath);
                Console.WriteLine("试听WAV: " + s.PreviewWav);
                float[] peaks = (s.Peaks != null && s.Peaks.Length > 0)
                              ? s.Peaks
                              : ex.BuildPeaks(s.PreviewWav, Extractor.PeakBuckets);
                double max = 0;
                for (int i = 0; i < peaks.Length; i++) { if (peaks[i] > max) { max = peaks[i]; } }
                Console.WriteLine("波形峰值点数: " + peaks.Length + " 最大=" + max.ToString("0.000"));
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("错误: " + e.Message);
                return 1;
            }
        }

        // ---- 从本地文件裁剪 ----
        private static int DoCut(Dictionary<string, string> o)
        {
            string src = Get(o, "cut");
            string outDir = Get(o, "out");
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(outDir)) { Console.WriteLine("错误: --cut 需要 --out"); return 2; }
            // 必须在 File.Exists 之前拦住 UNC —— 那一步就会发起 SMB 连接
            if (Extractor.IsUncPath(src))
            {
                Console.WriteLine("错误: 不支持网络路径（\\\\ 开头），请先把文件复制到本地磁盘。");
                return 2;
            }
            if (!File.Exists(src)) { Console.WriteLine("错误: 找不到文件 " + src); return 1; }
            double start = ParseDouble(Get(o, "start"), 0);
            double end = ParseDouble(Get(o, "end"), -1);
            if (end <= 0) { end = ParseDouble(Get(o, "dur"), 15); if (end <= 0) { end = 15; } }
            else { end = end - start; }
            // 与 GUI 保持一致：空选区/过短选区明确报参数错误，而不是生成一个几乎空的文件
            if (end <= 0.05)
            {
                Console.WriteLine("错误: 选区为空或过短（--end 必须大于 --start，且不短于 0.05 秒）。");
                return 2;
            }
            string name = Get(o, "name");
            if (string.IsNullOrEmpty(name)) { name = Path.GetFileNameWithoutExtension(src) + "_" + Fmt.Num(start) + "s-" + Fmt.Num(start + end) + "s"; }
            try
            {
                Directory.CreateDirectory(outDir);
                Extractor ex = MakeEngine();
                string p = ex.CutLocalFile(src, start, end, Get(o, "format"), outDir, name, WorkDir());
                double d = Extractor.ProbeDuration(p);
                Console.WriteLine("结果文件: " + p);
                Console.WriteLine("输出时长: " + d.ToString("0.00") + " 秒");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("错误: " + e.Message);
                return 1;
            }
        }

        // ---- 生成试听 wav ----
        private static int DoPreview(Dictionary<string, string> o)
        {
            string src = Get(o, "preview");
            string wav = Get(o, "wav");
            if (!File.Exists(src) || string.IsNullOrEmpty(wav)) { Console.WriteLine("错误: --preview 需要 --wav"); return 2; }
            int rate = (int)ParseDouble(Get(o, "rate"), 44100);
            try
            {
                string note;
                Extractor ex = MakeEngine();
                int code = ex.RunFfmpegPublic("-hide_banner -loglevel error -nostdin -y -i " + Extractor.Quote(src) +
                                              " -vn -ac 1 -ar " + rate + " -c:a pcm_s16le " + Extractor.Quote(wav), out note);
                if (code != 0) { Console.WriteLine("错误: ffmpeg 退出码 " + code + " " + note); return 1; }
                Console.WriteLine("已生成: " + wav + " (" + new FileInfo(wav).Length / 1024 + " KB)");
                return 0;
            }
            catch (Exception e) { Console.WriteLine("错误: " + e.Message); return 1; }
        }

        // ---- 波形峰值 ----
        private static int DoPeaks(Dictionary<string, string> o)
        {
            string wav = Get(o, "peaks");
            if (!File.Exists(wav)) { Console.WriteLine("错误: 找不到 " + wav); return 1; }
            int buckets = (int)ParseDouble(Get(o, "buckets"), 1000);
            double secs;
            float[] peaks = Extractor.ReadPeaksFromWav(wav, buckets, out secs);
            double max = 0, sum = 0;
            for (int i = 0; i < peaks.Length; i++) { if (peaks[i] > max) { max = peaks[i]; } sum += peaks[i]; }
            Console.WriteLine("点数=" + peaks.Length + "  时长=" + secs.ToString("0.00") + " 秒");
            Console.WriteLine("最大=" + max.ToString("0.000") + "  平均=" + (sum / peaks.Length).ToString("0.000"));
            StringBuilder bar = new StringBuilder();
            for (int i = 0; i < 80 && i < peaks.Length; i++)
            {
                int h = (int)(peaks[i * peaks.Length / 80] * 8);
                bar.Append(" .:-=+*#%@"[h]);
            }
            Console.WriteLine("波形示意: [" + bar + "]");
            return 0;
        }

        // ---- 旧版一次性片段 ----
        private static int DoOneShot(Dictionary<string, string> o)
        {
            ExtractionRequest req = new ExtractionRequest();
            req.Url = Get(o, "url");
            req.OutDir = Get(o, "out");
            if (string.IsNullOrEmpty(req.OutDir)) { req.OutDir = Environment.CurrentDirectory; }
            req.Start = ParseDouble(Get(o, "start"), 0);
            req.Duration = ParseDouble(Get(o, "dur"), 0);
            req.Format = Get(o, "format");
            req.NoSection = o.ContainsKey("no-section");
            try
            {
                Directory.CreateDirectory(req.OutDir);
                Extractor ex = MakeEngine();
                ExtractionResult r = ex.Run(req);
                Console.WriteLine("结果文件: " + r.FilePath);
                Console.WriteLine("输出时长: " + r.Duration.ToString("0.00") + " 秒");
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("错误: " + e.Message);
                return 1;
            }
        }

        private static Extractor MakeEngine()
        {
            Extractor ex = new Extractor();
            ex.Log += delegate(string s) { Console.WriteLine(s); };
            ex.Status += delegate(string s) { Console.WriteLine("[状态] " + s); };
            return ex;
        }

        private static string WorkDir()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "MediaAudioExtractor", "work");
            Directory.CreateDirectory(d);
            return d;
        }

        private static double ParseDouble(string s, double def)
        {
            double v;
            if (string.IsNullOrEmpty(s)) { return def; }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static string Get(Dictionary<string, string> o, string key)
        {
            string v;
            return o.TryGetValue(key, out v) ? v : null;
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            Dictionary<string, string> o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--")) { continue; }
                string key = a.Substring(2);
                string val = null;
                int eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    val = key.Substring(eq + 1);
                    key = key.Substring(0, eq);
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    val = args[++i];
                }
                o[key] = val;
            }
            return o;
        }
    }
}
