#include "desknext.h"
#include <stdarg.h>
#include <wincodec.h>
#include <map>

namespace dn {

// ====================================================== 日志

static SRWLOCK g_logLock = SRWLOCK_INIT;

// 日志写到 <数据根>\logs\shellext.log：本 DLL 位于 <数据根>\shellext\ 下，据此推导（目录名不符则不写日志）。
static std::wstring LogPath() {
    wchar_t buf[MAX_PATH * 2];
    DWORD n = GetModuleFileNameW(g_hMod, buf, ARRAYSIZE(buf));
    if (n == 0 || n >= ARRAYSIZE(buf)) return L"";
    std::wstring dir(buf);
    for (int i = 0; i < 2; ++i) { // 先去掉文件名，再取得 shellext 目录名
        size_t p = dir.find_last_of(L'\\');
        if (p == std::wstring::npos) return L"";
        if (i == 1 && _wcsicmp(dir.c_str() + p + 1, L"shellext") != 0) return L"";
        dir.resize(p);
    }
    dir += L"\\logs";
    CreateDirectoryW(dir.c_str(), nullptr);
    return dir + L"\\shellext.log";
}

static void LogImpl(const wchar_t* fmt, va_list ap) {
    wchar_t msg[1024];
    _vsnwprintf_s(msg, _TRUNCATE, fmt, ap);

    SYSTEMTIME st;
    GetLocalTime(&st);
    wchar_t line[1280];
    _snwprintf_s(line, _TRUNCATE, L"%04d-%02d-%02d %02d:%02d:%02d.%03d [P%lu T%lu] %s\r\n", st.wYear, st.wMonth, st.wDay,
                 st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, GetCurrentProcessId(), GetCurrentThreadId(), msg);

    AcquireSRWLockExclusive(&g_logLock);
    static std::wstring* path = nullptr;
    if (!path) path = new std::wstring(LogPath());
    if (!path->empty()) {
        HANDLE h = CreateFileW(path->c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                               OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE) {
            LARGE_INTEGER sz;
            if (GetFileSizeEx(h, &sz) && sz.QuadPart > 1024 * 1024) {
                // 超 1MB 截断
                SetFilePointer(h, 0, nullptr, FILE_BEGIN);
                SetEndOfFile(h);
            }
            std::string u = ToUtf8(line);
            DWORD w;
            WriteFile(h, u.data(), (DWORD)u.size(), &w, nullptr);
            CloseHandle(h);
        }
    }
    ReleaseSRWLockExclusive(&g_logLock);
}

void Log(const wchar_t* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    try { LogImpl(fmt, ap); } catch (...) { /* 日志永不影响宿主 */ }
    va_end(ap);
}

// ====================================================== 字符串

std::string ToUtf8(const std::wstring& s) {
    if (s.empty() || s.size() > (1u << 26)) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0, nullptr, nullptr);
    if (n <= 0) return std::string();
    std::string r((size_t)n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.data(), (int)s.size(), &r[0], n, nullptr, nullptr);
    return r;
}

std::wstring FromUtf8(const std::string& s) {
    if (s.empty() || s.size() > (1u << 26)) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
    if (n <= 0) return std::wstring();
    std::wstring r((size_t)n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &r[0], n);
    return r;
}

std::wstring HostProcessName() {
    wchar_t buf[MAX_PATH];
    DWORD n = GetModuleFileNameW(nullptr, buf, ARRAYSIZE(buf));
    if (n == 0 || n >= ARRAYSIZE(buf)) return L"";
    const wchar_t* p = wcsrchr(buf, L'\\');
    std::wstring name = p ? p + 1 : buf;
    for (auto& c : name) c = (wchar_t)towlower(c);
    return name;
}

// ====================================================== JSON

const JV* JV::Get(const char* key) const {
    if (t != Obj) return nullptr;
    for (auto& kv : o)
        if (kv.first == key) return &kv.second;
    return nullptr;
}
std::wstring JV::WStr(const char* key) const {
    const JV* v = Get(key);
    return (v && v->t == Str) ? FromUtf8(v->s) : std::wstring();
}
bool JV::GetBool(const char* key, bool def) const {
    const JV* v = Get(key);
    return (v && v->t == JV::Bool) ? v->b : def;
}
double JV::GetNum(const char* key, double def) const {
    const JV* v = Get(key);
    return (v && v->t == JV::Num) ? v->n : def;
}

namespace {
struct Parser {
    const char* p;
    const char* e;
    int depth = 0;

    void Ws() { while (p < e && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) ++p; }

    static void AppendUtf8(std::string& s, unsigned cp) {
        if (cp < 0x80) s += (char)cp;
        else if (cp < 0x800) { s += (char)(0xC0 | (cp >> 6)); s += (char)(0x80 | (cp & 0x3F)); }
        else if (cp < 0x10000) { s += (char)(0xE0 | (cp >> 12)); s += (char)(0x80 | ((cp >> 6) & 0x3F)); s += (char)(0x80 | (cp & 0x3F)); }
        else { s += (char)(0xF0 | (cp >> 18)); s += (char)(0x80 | ((cp >> 12) & 0x3F)); s += (char)(0x80 | ((cp >> 6) & 0x3F)); s += (char)(0x80 | (cp & 0x3F)); }
    }

    bool Hex4(unsigned& v) {
        if (e - p < 4) return false;
        v = 0;
        for (int i = 0; i < 4; i++) {
            char c = *p++;
            v <<= 4;
            if (c >= '0' && c <= '9') v |= c - '0';
            else if (c >= 'a' && c <= 'f') v |= c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') v |= c - 'A' + 10;
            else return false;
        }
        return true;
    }

    bool Str(std::string& out) {
        if (p >= e || *p != '"') return false;
        ++p;
        while (p < e) {
            char c = *p++;
            if (c == '"') return true;
            if (c != '\\') { out += c; continue; }
            if (p >= e) return false;
            char x = *p++;
            switch (x) {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    unsigned cp;
                    if (!Hex4(cp)) return false;
                    if (cp >= 0xD800 && cp < 0xDC00) {
                        if (e - p >= 6 && p[0] == '\\' && p[1] == 'u') {
                            p += 2;
                            unsigned lo;
                            if (!Hex4(lo)) return false;
                            if (lo >= 0xDC00 && lo < 0xE000) cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                            else cp = 0xFFFD;
                        } else cp = 0xFFFD;
                    }
                    AppendUtf8(out, cp);
                    break;
                }
                default: return false;
            }
        }
        return false;
    }

    bool Value(JV& v) {
        Ws();
        if (p >= e || depth > 32) return false;
        char c = *p;
        if (c == '{') {
            ++p; ++depth; v.t = JV::Obj;
            Ws();
            if (p < e && *p == '}') { ++p; --depth; return true; }
            for (;;) {
                Ws();
                std::string k;
                if (!Str(k)) return false;
                Ws();
                if (p >= e || *p != ':') return false;
                ++p;
                JV child;
                if (!Value(child)) return false;
                v.o.emplace_back(std::move(k), std::move(child));
                Ws();
                if (p < e && *p == ',') { ++p; continue; }
                if (p < e && *p == '}') { ++p; --depth; return true; }
                return false;
            }
        }
        if (c == '[') {
            ++p; ++depth; v.t = JV::Arr;
            Ws();
            if (p < e && *p == ']') { ++p; --depth; return true; }
            for (;;) {
                JV child;
                if (!Value(child)) return false;
                v.a.push_back(std::move(child));
                Ws();
                if (p < e && *p == ',') { ++p; continue; }
                if (p < e && *p == ']') { ++p; --depth; return true; }
                return false;
            }
        }
        if (c == '"') { v.t = JV::Str; return Str(v.s); }
        if (e - p >= 4 && !strncmp(p, "true", 4)) { p += 4; v.t = JV::Bool; v.b = true; return true; }
        if (e - p >= 5 && !strncmp(p, "false", 5)) { p += 5; v.t = JV::Bool; v.b = false; return true; }
        if (e - p >= 4 && !strncmp(p, "null", 4)) { p += 4; v.t = JV::Null; return true; }
        // 数字
        const char* s = p;
        while (p < e && (*p == '-' || *p == '+' || *p == '.' || *p == 'e' || *p == 'E' || (*p >= '0' && *p <= '9'))) ++p;
        if (p == s || p - s > 40) return false;
        std::string num(s, p);
        v.t = JV::Num;
        v.n = atof(num.c_str());
        return true;
    }
};
}  // namespace

bool JsonParse(const std::string& text, JV& out) {
    if (text.empty() || text.size() > (4u << 20)) return false;
    Parser ps{text.data(), text.data() + text.size()};
    out = JV();
    return ps.Value(out);
}

std::string JsonQuoteA(const std::string& u) {
    std::string r = "\"";
    for (unsigned char c : u) {
        switch (c) {
            case '"': r += "\\\""; break;
            case '\\': r += "\\\\"; break;
            case '\n': r += "\\n"; break;
            case '\r': r += "\\r"; break;
            case '\t': r += "\\t"; break;
            default:
                if (c < 0x20) { char b[8]; _snprintf_s(b, _TRUNCATE, "\\u%04x", c); r += b; }
                else r += (char)c;
        }
    }
    r += '"';
    return r;
}
std::string JsonQuote(const std::wstring& s) { return JsonQuoteA(ToUtf8(s)); }

// ====================================================== 管道客户端

static HANDLE OpenPipe(ULONGLONG deadline) {
    for (int i = 0; i < 2; i++) {
        HANDLE h = CreateFileW(DN_PIPE_NAME, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                               FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_ANONYMOUS, nullptr);
        if (h != INVALID_HANDLE_VALUE) return h;
        if (GetLastError() != ERROR_PIPE_BUSY) return INVALID_HANDLE_VALUE;
        ULONGLONG now = GetTickCount64();
        if (now >= deadline) return INVALID_HANDLE_VALUE;
        if (!WaitNamedPipeW(DN_PIPE_NAME, (DWORD)(deadline - now))) return INVALID_HANDLE_VALUE;
    }
    return INVALID_HANDLE_VALUE;
}

// 等待一次重叠 I/O，总期限 deadline；超时取消并返回 false
static bool WaitIo(HANDLE h, OVERLAPPED& ov, ULONGLONG deadline, DWORD& bytes) {
    ULONGLONG now = GetTickCount64();
    DWORD wait = deadline > now ? (DWORD)(deadline - now) : 0;
    if (WaitForSingleObject(ov.hEvent, wait) != WAIT_OBJECT_0) {
        CancelIoEx(h, &ov);
        GetOverlappedResult(h, &ov, &bytes, TRUE);
        return false;
    }
    return GetOverlappedResult(h, &ov, &bytes, FALSE) != 0;
}

static bool PipeCall(const std::string& req, std::string* resp, DWORD timeoutMs) {
    ULONGLONG deadline = GetTickCount64() + timeoutMs;
    HANDLE h = OpenPipe(deadline);
    if (h == INVALID_HANDLE_VALUE) return false;
    HANDLE ev = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!ev) { CloseHandle(h); return false; }
    bool ok = false;
    std::string line = req;
    if (line.empty() || line.back() != '\n') line += '\n';

    OVERLAPPED ov = {};
    ov.hEvent = ev;
    DWORD got = 0;
    BOOL w = WriteFile(h, line.data(), (DWORD)line.size(), nullptr, &ov);
    if (w || GetLastError() == ERROR_IO_PENDING) {
        if (WaitIo(h, ov, deadline, got)) {
            ok = true;
            if (resp) {
                ok = false;
                char buf[4096];
                for (;;) {
                    ResetEvent(ev);
                    OVERLAPPED rv = {};
                    rv.hEvent = ev;
                    BOOL r = ReadFile(h, buf, sizeof(buf), nullptr, &rv);
                    if (!r && GetLastError() != ERROR_IO_PENDING) break;
                    DWORD n = 0;
                    if (!WaitIo(h, rv, deadline, n) || n == 0) break;
                    resp->append(buf, n);
                    if (resp->size() > (4u << 20)) break;
                    if (resp->find('\n') != std::string::npos) { ok = true; break; }
                }
            }
        }
    }
    CloseHandle(ev);
    CloseHandle(h);
    return ok;
}

static bool SafePipeCall(const std::string& req, std::string* resp, DWORD timeoutMs) {
    bool ok = false;
    __try { ok = PipeCall(req, resp, timeoutMs); } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
    return ok;
}

bool PipeRoundTrip(const std::string& request, std::string& response, DWORD timeoutMs) {
    response.clear();
    return SafePipeCall(request, &response, timeoutMs);
}
bool PipeSend(const std::string& request, DWORD timeoutMs) {
    return SafePipeCall(request, nullptr, timeoutMs);
}

// ====================================================== 菜单位图（WIC → 32bpp PARGB）

static SRWLOCK g_bmpLock = SRWLOCK_INIT;
static std::map<std::wstring, HBITMAP>* g_bmpCache = nullptr;

static HBITMAP LoadViaWic(const std::wstring& path, int size) {
    IWICImagingFactory* fac = nullptr;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&fac)))) return nullptr;
    IWICBitmapDecoder* dec = nullptr;
    IWICBitmapFrameDecode* frame = nullptr;
    IWICBitmapScaler* scaler = nullptr;
    IWICFormatConverter* conv = nullptr;
    HBITMAP hbm = nullptr;
    do {
        if (FAILED(fac->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &dec))) break;
        // ICO 含多个尺寸的帧：挑不小于目标尺寸的最小帧（没有则取最大帧）；其他格式只有第 0 帧
        UINT count = 0, pick = 0, pickW = 0;
        if (FAILED(dec->GetFrameCount(&count)) || count == 0) break;
        if (count > 16) count = 16;
        for (UINT i = 0; i < count; i++) {
            IWICBitmapFrameDecode* f = nullptr;
            UINT w = 0, h = 0;
            if (FAILED(dec->GetFrame(i, &f)) || !f) continue;
            f->GetSize(&w, &h);
            f->Release();
            bool better = pickW == 0 || (pickW < (UINT)size && w > pickW) || (w >= (UINT)size && w < pickW);
            if (better) { pick = i; pickW = w; }
        }
        if (FAILED(dec->GetFrame(pick, &frame))) break;
        if (FAILED(fac->CreateBitmapScaler(&scaler))) break;
        if (FAILED(scaler->Initialize(frame, size, size, WICBitmapInterpolationModeFant))) break;
        if (FAILED(fac->CreateFormatConverter(&conv))) break;
        if (FAILED(conv->Initialize(scaler, GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom))) break;
        BITMAPINFO bi = {};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = size;
        bi.bmiHeader.biHeight = -size;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        void* bits = nullptr;
        hbm = CreateDIBSection(nullptr, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!hbm || !bits) { hbm = nullptr; break; }
        if (FAILED(conv->CopyPixels(nullptr, size * 4, size * size * 4, (BYTE*)bits))) { DeleteObject(hbm); hbm = nullptr; }
    } while (false);
    if (conv) conv->Release();
    if (scaler) scaler->Release();
    if (frame) frame->Release();
    if (dec) dec->Release();
    fac->Release();
    return hbm;
}

HBITMAP LoadMenuBitmap(const std::wstring& path) {
    if (path.empty()) return nullptr;
    HBITMAP result = nullptr;
    try {
        AcquireSRWLockExclusive(&g_bmpLock);
        if (!g_bmpCache) g_bmpCache = new std::map<std::wstring, HBITMAP>();
        auto it = g_bmpCache->find(path);
        if (it != g_bmpCache->end()) {
            result = it->second;
        } else {
            int size = GetSystemMetrics(SM_CXSMICON);
            if (size < 16) size = 16;
            result = LoadViaWic(path, size);
            (*g_bmpCache)[path] = result; // 失败也缓存 nullptr，避免反复解码
        }
    } catch (...) {
        result = nullptr;
    }
    ReleaseSRWLockExclusive(&g_bmpLock);
    return result;
}

}  // namespace dn
