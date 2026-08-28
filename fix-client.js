const fs = require("fs");
const p = "src/RemoteEternal.App/Services/SessionClient.cs";
let src = fs.readFileSync(p, "utf8");

// 1) Adiciona evento MediaFrameReceived após a propriedade Media
src = src.replace(
    "    public MediaBuffer Media { get; } = new();",
    `    public MediaBuffer Media { get; } = new();

    /// <summary>Frame de mídia cru recebido do host (formato: [flags(1)][ptsMs(8)][nalData]).
    /// Usado pelo pipeline de tempo real (decoder H.264 por parser).</summary>
    public event Action<byte[]>? MediaFrameReceived;`
);

// 2) Roteia TypeMedia para o evento (em vez do MediaBuffer)
src = src.replace(
    "                    case SecureFrameChannel.TypeMedia:\n                        Media.Write(payload, 0, payload.Length);\n                        break;",
    "                    case SecureFrameChannel.TypeMedia:\n                        MediaFrameReceived?.Invoke(payload);\n                        break;"
);

fs.writeFileSync(p, src, "utf8");
console.log("✅ SessionClient.cs: evento MediaFrameReceived");