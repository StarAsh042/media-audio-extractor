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
}
