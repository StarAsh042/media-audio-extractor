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
}
