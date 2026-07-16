using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Whisper.net;

namespace Flow;

/// <summary>
/// Wraps a loaded Whisper model. The factory (and model weights) stay in memory;
/// each call builds a lightweight processor. Serialized so overlapping dictations
/// can't collide.
/// </summary>
public sealed class Transcriber : IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly string _language;
    private readonly int _threads;
    private readonly int _audioContextSize;
    private readonly bool _adaptiveAudioContext;
    private readonly bool _singleSegment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _tmpWav;

    public Transcriber(string modelPath, string language, string tag = "main",
        int threads = 4, int audioContextSize = 0,
        bool adaptiveAudioContext = false, bool singleSegment = false)
    {
        _factory = WhisperFactory.FromPath(modelPath);
        _language = string.IsNullOrWhiteSpace(language) ? "en" : language;
        _threads = Math.Clamp(threads, 1, Math.Max(1, Environment.ProcessorCount));
        _audioContextSize = Math.Max(0, audioContextSize);
        _adaptiveAudioContext = adaptiveAudioContext;
        _singleSegment = singleSegment;
        _tmpWav = Path.Combine(AppSettings.DataDir, "capture_" + tag + ".wav");
    }

    /// <param name="pcm16kMono">16 kHz mono 16-bit PCM.</param>
    /// <param name="prompt">Optional initial prompt used to bias spelling (dictionary).</param>
    public async Task<string> TranscribeAsync(byte[] pcm16kMono, string? prompt = null,
        CancellationToken cancellationToken = default)
    {
        if (pcm16kMono.Length == 0) return string.Empty;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var writer = new WaveFileWriter(_tmpWav, new WaveFormat(16000, 16, 1)))
            {
                writer.Write(pcm16kMono, 0, pcm16kMono.Length);
            }

            // Whisper.net otherwise defaults to every logical processor. On the
            // 12-core ARM64 target that is dramatically slower (and much hotter)
            // than the small fixed thread counts selected by each caller.
            var builder = _factory.CreateBuilder()
                .WithLanguage(_language)
                .WithThreads(_threads);
            int audioContextSize = _adaptiveAudioContext
                ? AdaptiveAudioContext(pcm16kMono.Length)
                : _audioContextSize;
            if (audioContextSize > 0)
            {
                // Short recordings do not need Whisper's full 30-second context.
                // Scaling it to the input avoids wasted encoder work while longer
                // dictations retain enough context for their complete audio.
                builder = builder
                    .WithAudioContextSize(audioContextSize)
                    .WithNoContext();
            }
            if (_singleSegment || (_adaptiveAudioContext && audioContextSize > 0))
                builder = builder.WithSingleSegment();
            if (!string.IsNullOrWhiteSpace(prompt))
                builder = builder.WithPrompt(prompt);

            // Cancellation can leave Whisper processing briefly while the async
            // iterator unwinds. Whisper.net requires asynchronous disposal then.
            await using var processor = builder.Build();
            using var fs = File.OpenRead(_tmpWav);

            var sb = new StringBuilder();
            await foreach (var seg in processor.ProcessAsync(fs, cancellationToken).ConfigureAwait(false))
                sb.Append(seg.Text);

            return TextTools.StripTags(sb.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whisper encodes roughly 50 context frames per second of 16 kHz audio.
    /// Leave headroom at each tier and use the model's full context for long input.
    /// </summary>
    private static int AdaptiveAudioContext(int pcmBytes)
    {
        double seconds = pcmBytes / (16000d * 2d);
        if (seconds <= 8) return 512;
        if (seconds <= 16) return 1024;
        return 0;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _gate.Dispose();
    }
}
