const fs = require("fs");
const p = "src/RemoteEternal.App/Services/SessionHost.cs";
let src = fs.readFileSync(p, "utf8");

// Torna _mediaQueue recriável (não readonly)
src = src.replace(
    "    private readonly Channel<byte[]> _mediaQueue = Channel.CreateBounded<byte[]>(",
    "    private Channel<byte[]> _mediaQueue = Channel.CreateBounded<byte[]>("
);

// Recria a fila no início de StartCaptureAsync (antes do sender loop)
src = src.replace(
    `        // Drena frames antigos da fila e inicia o remetente ordenado.
        while (_mediaQueue.Reader.TryRead(out _)) { }
        _mediaSenderTask = Task.Run(MediaSenderLoopAsync);`,
    `        // Recria a fila FIFO e inicia o remetente ordenado (ordem H.264 é crítica).
        _mediaQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(60) { FullMode = BoundedChannelFullMode.Wait });
        _mediaSenderTask = Task.Run(MediaSenderLoopAsync);`
);

// Limpa o comentário obsoleto no StopCapture
src = src.replace(
    `        _mediaSenderTask = null;
        // Recria a fila para a próxima captura (a anterior foi completada).
        // (Channel não pode ser "des-completado", então criamos sob demanda.)
    }`,
    `        _mediaSenderTask = null;
    }`
);

fs.writeFileSync(p, src, "utf8");
console.log("✅ Channel recriável corrigido");