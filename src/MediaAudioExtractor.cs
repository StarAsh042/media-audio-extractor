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
}
