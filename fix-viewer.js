const fs = require("fs");
const p = "src/RemoteEternal.App/Views/ViewerWindow.xaml.cs";
let src = fs.readFileSync(p, "utf8");

// 0) using System.Buffers.Binary (para ler o cabeçalho do frame)
if (!src.includes("using System.Buffers.Binary;")) {
    src = src.replace("using System.Windows;", "using System.Buffers.Binary;\nusing System.Windows;");
}

// 1) Campo do decoder
src = src.replace(
    "    private FfmpegDecoder? _decoder;",
    "    private H264StreamDecoder? _decoder;"
);

// 2) Assina o evento de mídia (após MediaRestarted)
src = src.replace(
    "        _client.MediaRestarted += () => Dispatcher.InvokeAsync(OnMediaRestart);",
    `        _client.MediaRestarted += () => Dispatcher.InvokeAsync(OnMediaRestart);
        _client.MediaFrameReceived += OnMediaFrame;`
);

// 3) Substitui criação + assinaturas do decoder no StartDecoder
const oldBlock = `            var decoder = new FfmpegDecoder(_client.Media);
            decoder.ErrorOccurred += msg => Dispatcher.InvokeAsync(() => ShowError(msg));
            decoder.VideoFrameReady += OnVideoFrame;
            decoder.AudioReady += (pcm, rate, ch) =>
            {
                try
                {
                    _audio.SetFormat(rate, ch);
                    _audio.AddSamples(pcm, 0, pcm.Length);
                }
                catch
                {
                }
            };
            _decoder = decoder;
            decoder.Start();`;
const newBlock = `            var decoder = new H264StreamDecoder();
            decoder.VideoFrameReady += OnVideoFrame;
            _decoder = decoder;
            TxtStatus.Visibility = Visibility.Collapsed;`;
if (!src.includes(oldBlock)) { console.error("bloco decoder não encontrado"); process.exit(1); }
src = src.replace(oldBlock, newBlock);

// 4) Adiciona o método OnMediaFrame logo após o método OnMediaRestart
const restartMethod = `    private void OnMediaRestart()
    {
        TxtStatus.Visibility = Visibility.Visible;
        TxtStatus.Text = "Reiniciando vídeo...";
        StartDecoder();
    }`;
const withMediaFrame = restartMethod + `

    /// <summary>Recebe um frame H.264 cru do host ([flags(1)][ptsMs(8)][nalData]) e alimenta o decoder.</summary>
    private void OnMediaFrame(byte[] payload)
    {
        if (payload.Length < 9) return;
        bool isKey = (payload[0] & 1) != 0;
        long pts = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(1));
        var nal = new byte[payload.Length - 9];
        Buffer.BlockCopy(payload, 9, nal, 0, nal.Length);
        _decoder?.FeedPacket(nal, isKey, pts);
    }`;
if (!src.includes(restartMethod)) { console.error("OnMediaRestart não encontrado"); process.exit(1); }
src = src.replace(restartMethod, withMediaFrame);

fs.writeFileSync(p, src, "utf8");
console.log("✅ ViewerWindow.xaml.cs atualizado");