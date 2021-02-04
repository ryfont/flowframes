﻿using Flowframes.Media;
using Flowframes.Data;
using Flowframes.IO;
using Flowframes.Main;
using Flowframes.MiscUtils;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static Flowframes.AvProcess;
using Utils = Flowframes.Media.FFmpegUtils;

namespace Flowframes
{
    class FfmpegCommands
    {
        public static string divisionFilter = "pad=width=ceil(iw/2)*2:height=ceil(ih/2)*2:color=black@0";
        public static string pngComprArg = "-compression_level 3";
        public static string mpDecDef = "\"mpdecimate\"";
        public static string mpDecAggr = "\"mpdecimate=hi=64*32:lo=64*32:frac=0.1\"";

        public static async Task ConcatVideos(string concatFile, string outPath, int looptimes = -1)
        {
            Logger.Log($"Merging videos...", false, Logger.GetLastLine().Contains("frame"));
            string loopStr = (looptimes > 0) ? $"-stream_loop {looptimes}" : "";
            string vfrFilename = Path.GetFileName(concatFile);
            string args = $" {loopStr} -vsync 1 -f concat -i {vfrFilename} -c copy -movflags +faststart {outPath.Wrap()}";
            await RunFfmpeg(args, concatFile.GetParentDir(), LogMode.Hidden, TaskType.Merge);
        }

        public static async Task ExtractAlphaDir (string rgbDir, string alphaDir)
        {
            Directory.CreateDirectory(alphaDir);
            foreach (FileInfo file in IOUtils.GetFileInfosSorted(rgbDir))
            {
                string args = $"-i {file.FullName.Wrap()} -vf format=yuva444p16le,alphaextract,format=yuv420p {Path.Combine(alphaDir, file.Name).Wrap()}";
                await RunFfmpeg(args, LogMode.Hidden);
            }
        }

        public static async Task RemoveAlpha (string inputDir, string outputDir, string fillColor = "black")
        {
            Directory.CreateDirectory(outputDir);
            foreach (FileInfo file in IOUtils.GetFileInfosSorted(inputDir))
            {
                string outFilename = Path.Combine(outputDir, "_" + file.Name);
                Size res = IOUtils.GetImage(file.FullName).Size;
                string args = $" -f lavfi -i color={fillColor}:s={res.Width}x{res.Height} -i {file.FullName.Wrap()} " +
                    $"-filter_complex overlay=0:0:shortest=1 -pix_fmt rgb24 {outFilename.Wrap()}";
                await RunFfmpeg(args, LogMode.Hidden);
                file.Delete();
                File.Move(outFilename, file.FullName);
            }
        }

        public static async Task MergeAlphaIntoRgb (string rgbDir, int rgbPad, string alphaDir, int aPad, bool deleteAlphaDir)
        {
            string filter = "-filter_complex [0:v:0][1:v:0]alphamerge[out] -map [out]";
            string args = $"-i \"{rgbDir}/%{rgbPad}d.png\" -i \"{alphaDir}/%{aPad}d.png\" {filter} \"{rgbDir}/%{rgbPad}d.png\"";
            await RunFfmpeg(args, LogMode.Hidden);
            if (deleteAlphaDir)
                IOUtils.TryDeleteIfExists(alphaDir);
        }

        public static async Task LoopVideo(string inputFile, int times, bool delSrc = false)
        {
            string pathNoExt = Path.ChangeExtension(inputFile, null);
            string ext = Path.GetExtension(inputFile);
            string args = $" -stream_loop {times} -i {inputFile.Wrap()} -c copy \"{pathNoExt}-Loop{times}{ext}\"";
            await RunFfmpeg(args, LogMode.Hidden);
            if (delSrc)
                DeleteSource(inputFile);
        }

        public static async Task ChangeSpeed(string inputFile, float newSpeedPercent, bool delSrc = false)
        {
            string pathNoExt = Path.ChangeExtension(inputFile, null);
            string ext = Path.GetExtension(inputFile);
            float val = newSpeedPercent / 100f;
            string speedVal = (1f / val).ToString("0.0000").Replace(",", ".");
            string args = " -itsscale " + speedVal + " -i \"" + inputFile + "\"  -c copy \"" + pathNoExt + "-" + newSpeedPercent + "pcSpeed" + ext + "\"";
            await RunFfmpeg(args, LogMode.OnlyLastLine);
            if (delSrc)
                DeleteSource(inputFile);
        }

        public static long GetDuration(string inputFile)
        {
            Logger.Log("Reading Duration using ffprobe.", true, false, "ffmpeg");
            string args = $" -v panic -select_streams v:0 -show_entries format=duration -of csv=s=x:p=0 -sexagesimal {inputFile.Wrap()}";
            string info = GetFfprobeOutput(args);
            return FormatUtils.MsFromTimestamp(info);
        }

        public static float GetFramerate(string inputFile)
        {
            Logger.Log("Reading FPS using ffmpeg.", true, false, "ffmpeg");
            string args = $" -i {inputFile.Wrap()}";
            string output = GetFfmpegOutput(args);
            string[] entries = output.Split(',');
            foreach (string entry in entries)
            {
                if (entry.Contains(" fps") && !entry.Contains("Input "))    // Avoid reading FPS from the filename, in case filename contains "fps"
                {
                    Logger.Log("[FFCmds] FPS Entry: " + entry, true);
                    string num = entry.Replace(" fps", "").Trim().Replace(",", ".");
                    float value;
                    float.TryParse(num, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
                    return value;
                }
            }
            return 0f;
        }

        public static Size GetSize(string inputFile)
        {
            string args = $" -v panic -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 {inputFile.Wrap()}";
            string output = GetFfprobeOutput(args);

            if (output.Length > 4 && output.Contains("x"))
            {
                string[] numbers = output.Split('x');
                return new Size(numbers[0].GetInt(), numbers[1].GetInt());
            }
            return new Size(0, 0);
        }

        public static int GetFrameCount(string inputFile)
        {
            int frames = 0;

            Logger.Log("Reading frame count using ffprobe.", true, false, "ffmpeg");
            frames = ReadFrameCountFfprobe(inputFile, Config.GetBool("ffprobeCountFrames"));      // Try reading frame count with ffprobe
            if (frames > 0)
                return frames;

            Logger.Log($"Failed to get frame count using ffprobe (frames = {frames}). Reading frame count using ffmpeg.", true, false, "ffmpeg");
            frames = ReadFrameCountFfmpeg(inputFile);       // Try reading frame count with ffmpeg
            if (frames > 0)
                return frames;

            Logger.Log("Failed to get total frame count of video.");
            return 0;
        }

        public static async Task<int> GetFrameCountAsync(string inputFile)
        {
            int frames = 0;

            Logger.Log("Reading frame count using ffprobe.", true, false, "ffmpeg");
            frames = await ReadFrameCountFfprobeAsync(inputFile, Config.GetBool("ffprobeCountFrames"));      // Try reading frame count with ffprobe
            if (frames > 0)
                return frames;

            Logger.Log($"Failed to get frame count using ffprobe (frames = {frames}). Reading frame count using ffmpeg.", true, false, "ffmpeg");
            frames = await ReadFrameCountFfmpegAsync(inputFile);       // Try reading frame count with ffmpeg
            if (frames > 0)
                return frames;

            Logger.Log("Failed to get total frame count of video.");
            return 0;
        }

        static int ReadFrameCountFfprobe(string inputFile, bool readFramesSlow)
        {
            string args = $" -v panic -select_streams v:0 -show_entries stream=nb_frames -of default=noprint_wrappers=1 {inputFile.Wrap()}";
            if (readFramesSlow)
            {
                Logger.Log("Counting total frames using FFprobe. This can take a moment...");
                args = $" -v panic -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of default=nokey=1:noprint_wrappers=1 {inputFile.Wrap()}";
            }
            string info = GetFfprobeOutput(args);
            string[] entries = info.SplitIntoLines();
            try
            {
                if (readFramesSlow)
                    return info.GetInt();
                foreach (string entry in entries)
                {
                    if (entry.Contains("nb_frames="))
                        return entry.GetInt();
                }
            }
            catch { }
            return -1;
        }

        static async Task<int> ReadFrameCountFfprobeAsync(string inputFile, bool readFramesSlow)
        {
            string args = $" -v panic -select_streams v:0 -show_entries stream=nb_frames -of default=noprint_wrappers=1 {inputFile.Wrap()}";
            if (readFramesSlow)
            {
                Logger.Log("Counting total frames using FFprobe. This can take a moment...");
                await Task.Delay(10);
                args = $" -v panic -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of default=nokey=1:noprint_wrappers=1 {inputFile.Wrap()}";
            }
            string info = GetFfprobeOutput(args);
            string[] entries = info.SplitIntoLines();
            try
            {
                if (readFramesSlow)
                    return info.GetInt();
                foreach (string entry in entries)
                {
                    if (entry.Contains("nb_frames="))
                        return entry.GetInt();
                }
            }
            catch { }
            return -1;
        }

        static int ReadFrameCountFfmpeg(string inputFile)
        {
            string args = $" -loglevel panic -i {inputFile.Wrap()} -map 0:v:0 -c copy -f null - ";
            string info = GetFfmpegOutput(args);
            string[] entries = info.SplitIntoLines();
            foreach (string entry in entries)
            {
                if (entry.Contains("frame="))
                    return entry.Substring(0, entry.IndexOf("fps")).GetInt();
            }
            return -1;
        }

        static async Task<int> ReadFrameCountFfmpegAsync (string inputFile)
        {
            string args = $" -loglevel panic -i {inputFile.Wrap()} -map 0:v:0 -c copy -f null - ";
            string info = await GetFfmpegOutputAsync(args, true);
            try
            {
                string[] lines = info.SplitIntoLines();
                string lastLine = lines.Last();
                return lastLine.Substring(0, lastLine.IndexOf("fps")).GetInt();
            }
            catch
            {
                return -1;
            }
        }

        public static string GetAudioCodec(string path)
        {
            string args = $" -v panic -show_streams -select_streams a -show_entries stream=codec_name {path.Wrap()}";
            string info = GetFfprobeOutput(args);
            string[] entries = info.SplitIntoLines();
            foreach (string entry in entries)
            {
                if (entry.Contains("codec_name="))
                    return entry.Split('=')[1];
            }
            return "";
        }

        public static void DeleteSource(string path)
        {
            Logger.Log("[FFCmds] Deleting input file/dir: " + path, true);

            if (IOUtils.IsPathDirectory(path) && Directory.Exists(path))
                Directory.Delete(path, true);

            if (!IOUtils.IsPathDirectory(path) && File.Exists(path))
                File.Delete(path);
        }
    }
}