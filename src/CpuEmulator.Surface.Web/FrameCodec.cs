using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CpuEmulator.Core;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// The SP0 WebSocket wire format. Frames OUT: a small binary header ('F','B', version, reserved,
/// uint16 width LE, uint16 height LE) followed by width*height little-endian RGBA8888 pixels (raw —
/// no delta/RLE in SP0; one local client at 256×192 is well within bandwidth). Keys IN: JSON text
/// {"action","code","char"} where "code" is the DOM KeyboardEvent.code; <see cref="MapDomCode"/>
/// normalizes it to a portable <see cref="KeyCode"/> (unknown -> <see cref="KeyCode.None"/>).
/// </summary>
public static class FrameCodec
{
    private const int HeaderBytes = 8;

    public static byte[] EncodeFrame(int width, int height, ReadOnlySpan<uint> pixels)
    {
        if (pixels.Length < width * height)
            throw new ArgumentException("pixel buffer smaller than width*height", nameof(pixels));

        var frame = new byte[HeaderBytes + width * height * 4];
        frame[0] = (byte)'F';
        frame[1] = (byte)'B';
        frame[2] = 0x01; // version
        frame[3] = 0x00; // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), (ushort)height);

        Span<byte> body = frame.AsSpan(HeaderBytes);
        for (int i = 0; i < width * height; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(i * 4, 4), pixels[i]);
        return frame;
    }

    private const int AudioHeaderBytes = 8;

    /// <summary>Encode one S16 audio frame for the WebSocket. Wire shape mirrors the FB header size:
    /// [0]='A' [1]='U' [2]=version(1) [3]=channelCount, [4..7]=u32 sampleCount (per channel * channels,
    /// i.e. the total short count) LE, then <paramref name="samples"/> as little-endian S16. The host
    /// sample rate is a fixed contract constant (44100) shared with the browser client, so it is not
    /// carried per frame.</summary>
    public static byte[] EncodeAudio(int sampleRate, int channels, ReadOnlySpan<short> samples)
    {
        _ = sampleRate; // fixed-rate contract; kept in the signature for call-site clarity
        var frame = new byte[AudioHeaderBytes + samples.Length * 2];
        frame[0] = (byte)'A';
        frame[1] = (byte)'U';
        frame[2] = 0x01;                 // version
        frame[3] = (byte)channels;       // channel count
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), (uint)samples.Length);

        Span<byte> body = frame.AsSpan(AudioHeaderBytes);
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(body.Slice(i * 2, 2), samples[i]);
        return frame;
    }

    /// <summary>Encode a machine-status snapshot as the <c>ST</c> text frame: the literal prefix
    /// <c>"ST "</c> (the existing client contract — app.js routes every text frame to handleStatusText
    /// and gates on "ST ") followed by a compact JSON body. Text, not binary: the FB/AU binary path is
    /// untouched; the client's text branch already owns this. JSON keys are lower-case + stable so equal
    /// snapshots produce byte-identical frames (the host's change-detection compares the encoded bytes).
    /// </summary>
    public static byte[] EncodeStatus(MachineStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var body = new
        {
            board = status.Board,
            asset = status.Asset,
            mode = status.Mode,
            drives = status.Drives.Select(d => new { motor = d.MotorOn, label = d.Label }).ToArray(),
        };
        // No indented/whitespace options -> deterministic compact JSON (equal snapshots -> equal bytes).
        string json = JsonSerializer.Serialize(body);
        return Encoding.UTF8.GetBytes("ST " + json);
    }

    public static bool TryDecodeKey(string json, out KeyEvent e)
    {
        e = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string action = root.TryGetProperty("action", out JsonElement a) ? a.GetString() ?? "" : "";
            string code = root.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "" : "";
            string charStr = root.TryGetProperty("char", out JsonElement ch) ? ch.GetString() ?? "" : "";

            KeyAction keyAction = action == "up" ? KeyAction.Up : KeyAction.Down;
            KeyCode key = MapDomCode(code);
            char? typed = charStr.Length == 1 ? charStr[0] : null;
            e = new KeyEvent(keyAction, key, typed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Map a DOM <c>KeyboardEvent.code</c> to a portable <see cref="KeyCode"/>. Unknown
    /// codes return <see cref="KeyCode.None"/> (the machine ignores them).</summary>
    public static KeyCode MapDomCode(string code) => code switch
    {
        "KeyA" => KeyCode.A, "KeyB" => KeyCode.B, "KeyC" => KeyCode.C, "KeyD" => KeyCode.D,
        "KeyE" => KeyCode.E, "KeyF" => KeyCode.F, "KeyG" => KeyCode.G, "KeyH" => KeyCode.H,
        "KeyI" => KeyCode.I, "KeyJ" => KeyCode.J, "KeyK" => KeyCode.K, "KeyL" => KeyCode.L,
        "KeyM" => KeyCode.M, "KeyN" => KeyCode.N, "KeyO" => KeyCode.O, "KeyP" => KeyCode.P,
        "KeyQ" => KeyCode.Q, "KeyR" => KeyCode.R, "KeyS" => KeyCode.S, "KeyT" => KeyCode.T,
        "KeyU" => KeyCode.U, "KeyV" => KeyCode.V, "KeyW" => KeyCode.W, "KeyX" => KeyCode.X,
        "KeyY" => KeyCode.Y, "KeyZ" => KeyCode.Z,
        "Digit0" => KeyCode.Digit0, "Digit1" => KeyCode.Digit1, "Digit2" => KeyCode.Digit2,
        "Digit3" => KeyCode.Digit3, "Digit4" => KeyCode.Digit4, "Digit5" => KeyCode.Digit5,
        "Digit6" => KeyCode.Digit6, "Digit7" => KeyCode.Digit7, "Digit8" => KeyCode.Digit8,
        "Digit9" => KeyCode.Digit9,
        "Space" => KeyCode.Space,
        "Enter" => KeyCode.Enter,
        "Backspace" => KeyCode.Backspace,
        "Tab" => KeyCode.Tab,
        "Escape" => KeyCode.Escape,
        "ArrowLeft" => KeyCode.ArrowLeft,
        "ArrowRight" => KeyCode.ArrowRight,
        "ArrowUp" => KeyCode.ArrowUp,
        "ArrowDown" => KeyCode.ArrowDown,
        _ => KeyCode.None,
    };
}
