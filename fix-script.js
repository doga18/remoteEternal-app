const fs = require("fs");
const path = "src/RemoteEternal.App/Media/FfmpegDecoder.cs";
let src = fs.readFileSync(path, "utf8");

// Remove o bloco malformado (comentário + catch em escopo de classe)
const broken = `    // Diagnóstico do decoder: expõe o erro real em vez de engolir nocatch (Exception ex)
{
    var msg = $"Decoder falhou: {ex.Message}";
    DiagnosticLog.Write("FfmpegDecoder", msg);
    try { ErrorOccurred?.Invoke(msg); } catch { }
}
`;
if (!src.includes(broken)) { console.error("bloco quebrado não encontrado"); process.exit(1); }
src = src.replace(broken, "");
fs.writeFileSync(path, src, "utf8");
console.log("✅ Bloco quebrado removido");