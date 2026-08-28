const fs = require("fs");
const path = "src/RemoteEternal.App/Media/FfmpegDecoder.cs";
let src = fs.readFileSync(path, "utf8");

// 1) Adiciona ResetAbort() ao MediaBuffer (após WakeAbort)
const oldWake = `    public void WakeAbort()
    {
        lock (_lock)
        {
            _wakeAbort = true;
            Monitor.PulseAll(_lock);
        }
    }`;
const newWake = `    public void WakeAbort()
    {
        lock (_lock)
        {
            _wakeAbort = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Limpa o estado de abort, permitindo que um novo decoder use o buffer.</summary>
    public void ResetAbort()
    {
        lock (_lock)
        {
            _wakeAbort = false;
            Monitor.PulseAll(_lock);
        }
    }`;
if (!src.includes(oldWake)) { console.error("WakeAbort não encontrado"); process.exit(1); }
src = src.replace(oldWake, newWake);

// 2) No Dispose, reseta o abort após a thread sair
const oldDispose = `        _buffer.WakeAbort();
        if (!_threadDone.Wait(3000)) return; // thread não saiu; evita liberar memória em uso`;
const newDispose = `        _buffer.WakeAbort();
        bool threadExited = _threadDone.Wait(3000);
        if (!threadExited) return; // thread não saiu; evita liberar memória em uso
        // FIX: reseta o abort do buffer compartilhado para não envenenar o próximo decoder.
        _buffer.ResetAbort();`;
if (!src.includes(oldDispose)) { console.error("Dispose WakeAbort não encontrado"); process.exit(1); }
src = src.replace(oldDispose, newDispose);

fs.writeFileSync(path, src, "utf8");
console.log("✅ ResetAbort adicionado + Dispose reseta abort");