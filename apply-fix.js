const fs = require("fs");

// ===== 1) MediaBuffer.Clear(): reseta _wakeAbort (fix do buffer envenenado) =====
let dec = fs.readFileSync("src/RemoteEternal.App/Media/FfmpegDecoder.cs", "utf8");
const oldClear = `    public void Clear()
    {
        lock (_lock)
        {
            _segments.Clear();
            _length = 0;
            Monitor.PulseAll(_lock);
        }
    }`;
const newClear = `    public void Clear()
    {
        lock (_lock)
        {
            _segments.Clear();
            _length = 0;
            _wakeAbort = false; // FIX: não deixa o buffer "envenenado" pelo decoder anterior
            Monitor.PulseAll(_lock);
        }
    }`;
if (!dec.includes(oldClear)) { console.error("Clear não encontrado"); process.exit(1); }
dec = dec.replace(oldClear, newClear);

// ===== 2) ReadCallback: log de EOF para diagnóstico =====
const oldReadCb = `    private static int ReadCallback(void* opaque, byte* buffer, int bufferSize)
    {
        var self = (FfmpegDecoder?)GCHandle.FromIntPtr((IntPtr)opaque).Target;
        if (self is null || self._disposed) return AVERROR_EOF;
        long pos = self._avio->pos;
        byte[] tmp = new byte[bufferSize];
        int n = self._buffer.ReadAt(pos, tmp, bufferSize);
        if (n <= 0) return AVERROR_EOF;
        Marshal.Copy(tmp, 0, (IntPtr)buffer, n);
        self.RecordMediaDump(tmp, n);
        return n;
    }`;
const newReadCb = `    private static int ReadCallback(void* opaque, byte* buffer, int bufferSize)
    {
        var self = (FfmpegDecoder?)GCHandle.FromIntPtr((IntPtr)opaque).Target;
        if (self is null || self._disposed) return AVERROR_EOF;
        long pos = self._avio->pos;
        byte[] tmp = new byte[bufferSize];
        int n = self._buffer.ReadAt(pos, tmp, bufferSize);
        if (n <= 0)
        {
            if (!self._eofLogged)
            {
                self._eofLogged = true;
                DiagnosticLog.Write("FfmpegDecoder", $"ReadCallback EOF/sem dados em pos={pos} (len={self._buffer.Length})");
            }
            return AVERROR_EOF;
        }
        Marshal.Copy(tmp, 0, (IntPtr)buffer, n);
        self.RecordMediaDump(tmp, n);
        return n;
    }`;
if (!dec.includes(oldReadCb)) { console.error("ReadCallback não encontrado"); process.exit(1); }
dec = dec.replace(oldReadCb, newReadCb);

// adiciona o campo _eofLogged junto aos campos do decoder
const fieldMarker = "    private volatile bool _disposed;";
if (!dec.includes(fieldMarker)) { console.error("campo _disposed não encontrado"); process.exit(1); }
dec = dec.replace(fieldMarker, "    private volatile bool _disposed;\n    private bool _eofLogged;");

fs.writeFileSync("src/RemoteEternal.App/Media/FfmpegDecoder.cs", dec);
console.log("✅ FfmpegDecoder.cs: Clear reset + log de EOF");

// ===== 3) AppState: versão 1.0.4 =====
let appState = fs.readFileSync("src/RemoteEternal.App/Services/AppState.cs", "utf8");
appState = appState.replace('public const string AppVersion = "1.0.2";', 'public const string AppVersion = "1.0.4";');
fs.writeFileSync("src/RemoteEternal.App/Services/AppState.cs", appState);
console.log("✅ AppState.cs: AppVersion 1.0.4");

// ===== 4) MainWindow.xaml: Title com versão =====
let mw = fs.readFileSync("src/RemoteEternal.App/Views/MainWindow.xaml", "utf8");
mw = mw.replace('Title="RemoteEternal"', 'Title="RemoteEternal 1.0.4"');
fs.writeFileSync("src/RemoteEternal.App/Views/MainWindow.xaml", mw);
console.log("✅ MainWindow.xaml: Title com 1.0.4");