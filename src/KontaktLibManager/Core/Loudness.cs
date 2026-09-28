using System;
using System.IO;
using NAudio.Wave;

namespace KontaktLibManager.Core;

/// <summary>
/// **试听响度归一化**（2026-09-26 新增）。
///
/// 为什么需要（用户实测反馈）：
///   交响采样库的多麦位里，**Hall / Decca 是远距离麦位、录制电平天然比 Close 低 15~25 dB**，
///   而 `suscymbal`（吊镲持续音）这类本身设计得就很轻。
///   实测用户「必须把音箱音量放到最大才能听到」—— 连续听几个样本还要反复调音量，很烦。
///
/// 做法：**按 RMS 把整体响度拉到统一目标，同时把峰值压在 -1 dBFS 以内**。
///   · 只做「整体增益」，**不做压缩/限幅** ⇒ 不动原本的力度层次（试听只需要听清，不需要重塑）
///   · 增益上限 +30 dB、下限 -30 dB ⇒ 避免把近乎静音的坏样本放大成噪声
///   · 已经是目标响度的样本几乎不变 ⇒ 不会破坏「Close 比 Hall 响」这种设计意图的**相对**关系
///     （⚠️ 但我们确实会拉平绝对差异 —— 这是试听的取舍：先能听清）
/// </summary>
public static class Loudness
{
    /// <summary>目标 RMS（dBFS）。-20 大约是「正常说话的录音电平」，试听够清楚又不刺耳。</summary>
    private const double TargetRmsDb = -20.0;
    /// <summary>峰值上限（dBFS）—— 留 1 dB 余量，避免近似削波。</summary>
    private const double PeakCeilingDb = -1.0;
    /// <summary>增益上下限（dB）—— 防止把静音样本放大成噪声，也避免把很响的压得过低。</summary>
    private const double MaxGainDb = 30.0, MinGainDb = -30.0;

    /// <summary>
    /// **就地归一化一个 WAV 文件**（16-bit PCM，NAudio 可读写）。
    /// 失败时**静默返回**（归一化只是锦上添花，绝不能因为它让试听失败）。
    /// </summary>
    public static void NormalizeWavInPlace(string wavPath)
    {
        try
        {
            if (!File.Exists(wavPath)) return;
            var fi = new FileInfo(wavPath);
            // 只处理小文件（试听片段本来就短；避免误伤大文件）
            if (fi.Length <= 0 || fi.Length > 64L * 1024 * 1024) return;

            float[] samples;
            WaveFormat fmt;
            using (var reader = new AudioFileReader(wavPath))
            {
                fmt = reader.WaveFormat;
                // 只处理单/双声道（试听采样都是这类）
                if (fmt.Channels < 1 || fmt.Channels > 2) return;
                var buf = new float[reader.Length / 4 + 1024];
                int total = 0, n;
                while ((n = reader.Read(buf, total, buf.Length - total)) > 0)
                {
                    total += n;
                    if (total >= buf.Length) break;
                }
                if (total == 0) return;
                samples = new float[total];
                Array.Copy(buf, samples, total);
            }

            // ① 算 RMS 与峰值
            double sumSq = 0; float peak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                double v = samples[i];
                sumSq += v * v;
                float av = Math.Abs(samples[i]);
                if (av > peak) peak = av;
            }
            double rms = Math.Sqrt(sumSq / samples.Length);
            if (rms <= 1e-7 || peak <= 1e-7) return;   // 近乎静音/全零 ⇒ 不动

            // ② 目标增益 = 让 RMS 到 TargetRmsDb
            double gain = Math.Pow(10, TargetRmsDb / 20.0) / rms;
            // ③ 若会超过峰值上限，则改为「刚好不削波」
            double peakAfter = peak * gain;
            double ceiling = Math.Pow(10, PeakCeilingDb / 20.0);
            if (peakAfter > ceiling) gain = ceiling / peak;
            // ④ 夹取增益范围
            double gDb = 20 * Math.Log10(gain);
            gDb = Math.Clamp(gDb, MinGainDb, MaxGainDb);
            gain = Math.Pow(10, gDb / 20.0);
            // ⑤ 变化不到 0.5 dB 就别重写了（省 IO）
            if (Math.Abs(gDb) < 0.5) return;

            // ⑥ 应用增益并写回（16-bit PCM）
            for (int i = 0; i < samples.Length; i++)
            {
                double v = samples[i] * gain;
                v = Math.Clamp(v, -1.0, 1.0);
                samples[i] = (float)v;
            }
            string tmp = wavPath + ".norm.tmp";
            var outFmt = new WaveFormat(fmt.SampleRate, 16, fmt.Channels);
            using (var w = new WaveFileWriter(tmp, outFmt))
            {
                w.WriteSamples(samples, 0, samples.Length);
            }
            File.Move(tmp, wavPath, overwrite: true);
        }
        catch
        {
            // 静默失败：归一化不是必需功能
        }
    }
}
