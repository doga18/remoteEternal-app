const fs = require("fs");
const path = require("path");

// 1) FfmpegDecoder.cs: captura erro no DecodeLoop e dispara evento
let src = fs.readFileSync("src/RemoteEternal.App/Media/FfmpegDecoder.cs", "utf8");
const oldCatch = /\s*catch\s*\{\s*\}/;
const newCatch = `catch (Exception ex)
{
    var msg = $"Decoder falhou: {ex.Message}";
    DiagnosticLog.Write("FfmpegDecoder", msg);
    try { ErrorOccurred?.Invoke(msg); } catch { }
}`;
if (!oldCatch.test(src)) { console.error("Catch não encontrado em FfmpegDecoder.cs"); process.exit(1); }
src = src.replace(oldCatch, newCatch);
fs.writeFileSync("src/RemoteEternal.App/Media/FfmpegDecoder.cs", src);
console.log("✅ FfmpegDecoder.cs atualizado com diagnóstico");

// 2) ViewerWindow.xaml.cs: assina o evento ErrorOccurred
src = fs.readFileSync("src/RemoteEternal.App/Views/ViewerWindow.xaml.cs", "utf8");
const oldLine = /var decoder = new FfmpegDecoder\(_client\.Media\);\s*\n\s*decoder\.VideoFrameReady \+= OnVideoFrame;/;
const newLine = `var decoder = new FfmpegDecoder(_client.Media);
            decoder.ErrorOccurred += msg => Dispatcher.InvokeAsync(() => ShowError(msg));
            decoder.VideoFrameReady += OnVideoFrame;`;
if (!oldLine.test(src)) { console.error("Padrão não encontrado em ViewerWindow.xaml.cs"); process.exit(1); }
src = src.replace(oldLine, newLine);
fs.writeFileSync("src/RemoteEternal.App/Views/ViewerWindow.xaml.cs", src);
console.log("✅ ViewerWindow.xaml.cs atualizado com assinatura de erro");