const fs = require("fs");
const p = "src/RemoteEternal.App/Services/SessionHost.cs";
let src = fs.readFileSync(p, "utf8");
const broken = `            DiagnosticLog.Write("SessionCapture", "MediaSenderLoop: " + ex.GetType().Name);
        }
    private void StopCapture()`;
const fixed = `            DiagnosticLog.Write("SessionCapture", "MediaSenderLoop: " + ex.GetType().Name);
        }
    }

    private void StopCapture()`;
if (!src.includes(broken)) { console.error("padrão não encontrado"); process.exit(1); }
src = src.replace(broken, fixed);
fs.writeFileSync(p, src, "utf8");
console.log("ok");