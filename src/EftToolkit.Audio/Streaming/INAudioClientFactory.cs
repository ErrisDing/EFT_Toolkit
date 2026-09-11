using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EftToolkit.Audio.Streaming;

/// <summary>
/// Opens the two clients a stream needs. This is the session's only contact with NAudio's device
/// layer, which is what lets the lifecycle rules — one capture, one render, both shared, everything
/// released on stop — be tested without taking a real endpoint away from the machine running the
/// tests.
/// </summary>
internal interface INAudioClientFactory
{
    /// <summary>Opens the virtual capture endpoint the game's audio arrives on.</summary>
    ISharedCaptureClient CreateCapture(string endpointId);

    /// <summary>Opens the physical render endpoint the user listens to.</summary>
    ISharedRenderClient CreateRender(string endpointId, int latencyMilliseconds);
}

/// <summary>A capture endpoint, open in shared mode.</summary>
internal interface ISharedCaptureClient : IAsyncDisposable
{
    WaveFormat WaveFormat { get; }

    /// <summary>The buffer the endpoint was opened with, for reporting.</summary>
    int BufferMilliseconds { get; }

    event EventHandler<CapturedAudioEventArgs>? DataAvailable;

    /// <summary>Raised when capture stops for a reason other than a requested stop.</summary>
    event EventHandler<Exception>? Faulted;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>A render endpoint, open in shared mode.</summary>
internal interface ISharedRenderClient : IAsyncDisposable
{
    /// <summary>The device mix format, which is what the render side will accept.</summary>
    WaveFormat MixFormat { get; }

    /// <summary>The buffer the endpoint was opened with, for reporting.</summary>
    int BufferMilliseconds { get; }

    /// <summary>Raised when playback stops for a reason other than a requested stop.</summary>
    event EventHandler<Exception>? Faulted;

    /// <summary>
    /// Starts pulling from the source. The share mode is a parameter rather than a decision made
    /// here, so the one caller has to name shared mode and there is nowhere for a silent fallback
    /// to exclusive mode to hide.
    /// </summary>
    Task StartAsync(IWaveProvider provider, AudioClientShareMode shareMode, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One captured block. <see cref="Buffer"/> is the whole buffer the endpoint handed over and
/// <see cref="BytesRecorded"/> is how much of it is real, because NAudio reuses its buffer.
/// </summary>
internal sealed record CapturedAudioEventArgs(ReadOnlyMemory<byte> Buffer, int BytesRecorded);
