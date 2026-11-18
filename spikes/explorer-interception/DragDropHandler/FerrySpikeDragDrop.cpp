// Ferry spike (C): right-drag DragDropHandler shell extension. THROWAWAY prototype, never shipped.
//
// What it does: when the user RIGHT-drags files onto a folder, it adds two items to the drop menu:
// "Copy here with Ferry (test)" and "Move here with Ferry (test)". Choosing one sends the source list,
// target folder and copy/move to a local named pipe (watch-counter.ps1 listens) and returns at once.
// It never copies, moves or deletes anything. If the pipe is not there, it only logs that the
// production version would fall back to the stock Windows copy.
//
// Design rules this prototype demonstrates (all must hold in production):
//  - Runs inside Explorer, so: no exceptions escape, no blocking without a timeout, tiny and stateless.
//  - Every COM method is wrapped in try/catch and returns an HRESULT.
//  - Pipe I/O is overlapped with a hard timeout; the client asks for SECURITY_IDENTIFICATION only.
//  - Nothing is done unless the target is a normal file-system folder and the data has CF_HDROP.

#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <strsafe.h>
#include <new>
#include <string>
#include <vector>

namespace {

// {4A158046-8ED4-476F-B58B-19D7B7D775FE}
const CLSID CLSID_FerrySpikeDragDrop = {0x4a158046, 0x8ed4, 0x476f, {0xb5, 0x8b, 0x19, 0xd7, 0xb7, 0xd7, 0x75, 0xfe}};

const wchar_t kPipeName[] = L"\\\\.\\pipe\\FerrySpike-DragDrop";
// Total time budget for the whole hand-off on Explorer's UI thread (connect wait + write), kept under the
// 350 ms "helper down or hung" target with margin for the cancel and the log write.
const DWORD kPipeBudgetMs = 250;
const DWORD kPipeConnectWaitMs = 100;
const size_t kMaxItems = 100000;

HMODULE g_module = nullptr;
volatile LONG g_objects = 0;      // live handler objects
volatile LONG g_locks = 0;        // IClassFactory::LockServer count
volatile LONG g_factoryRefs = 0;  // outstanding references to the class factory
volatile LONG g_menuCount = 0;
volatile LONG g_invokeCount = 0;

enum : UINT { kCmdCopy = 0, kCmdMove = 1, kCmdCount = 2 };

// Appends one line to %LOCALAPPDATA%\FerrySpike\dragdrop.log. Best effort; never throws.
void LogLine(const std::wstring& text) noexcept {
    try {
        wchar_t* base = nullptr;
        if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &base))) return;
        std::wstring dir = std::wstring(base) + L"\\FerrySpike";
        CoTaskMemFree(base);
        CreateDirectoryW(dir.c_str(), nullptr);
        std::wstring file = dir + L"\\dragdrop.log";

        SYSTEMTIME st;
        GetLocalTime(&st);
        wchar_t stamp[64];
        StringCchPrintfW(stamp, ARRAYSIZE(stamp), L"%04u-%02u-%02u %02u:%02u:%02u.%03u pid=%lu ",
                         st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                         GetCurrentProcessId());
        std::wstring line = stamp + text + L"\r\n";

        int bytes = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
        if (bytes <= 0) return;
        std::string utf8(static_cast<size_t>(bytes), '\0');
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), &utf8[0], bytes, nullptr, nullptr);

        HANDLE h = CreateFileW(file.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                               OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return;
        DWORD written = 0;
        WriteFile(h, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        CloseHandle(h);
    } catch (...) {
    }
}

// Sends the job to the pipe. Returns a short status for the log. Whole call stays within kPipeBudgetMs
// (plus the time CancelIoEx takes to complete, which is immediate for a pipe write).
std::wstring SendToPipe(const std::wstring& message) noexcept {
    try {
        const ULONGLONG deadline = GetTickCount64() + kPipeBudgetMs;
        DWORD flags = FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION;
        HANDLE pipe = CreateFileW(kPipeName, GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, flags, nullptr);
        if (pipe == INVALID_HANDLE_VALUE && GetLastError() == ERROR_PIPE_BUSY) {
            if (WaitNamedPipeW(kPipeName, kPipeConnectWaitMs)) {
                pipe = CreateFileW(kPipeName, GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, flags, nullptr);
            }
        }
        if (pipe == INVALID_HANDLE_VALUE) {
            wchar_t buf[96];
            StringCchPrintfW(buf, ARRAYSIZE(buf), L"pipe=down(err %lu) -> production would fall back to stock copy", GetLastError());
            return buf;
        }

        // Production must also check the server is Ferry's signed helper (GetNamedPipeServerProcessId +
        // image path/signature) before sending anything. The spike only records the PID.
        ULONG serverPid = 0;
        GetNamedPipeServerProcessId(pipe, &serverPid);

        OVERLAPPED ov = {};
        ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        std::wstring result;
        if (!ov.hEvent) {
            result = L"pipe=error(no event) -> fallback";
        } else {
            DWORD bytes = static_cast<DWORD>(message.size() * sizeof(wchar_t));
            DWORD written = 0;
            BOOL ok = WriteFile(pipe, message.data(), bytes, &written, &ov);
            if (!ok && GetLastError() == ERROR_IO_PENDING) {
                ULONGLONG now = GetTickCount64();
                DWORD remaining = now < deadline ? static_cast<DWORD>(deadline - now) : 0;
                if (WaitForSingleObject(ov.hEvent, remaining) == WAIT_OBJECT_0) {
                    ok = GetOverlappedResult(pipe, &ov, &written, FALSE);
                } else {
                    CancelIoEx(pipe, &ov);
                    GetOverlappedResult(pipe, &ov, &written, TRUE);  // wait for the cancel to finish
                    ok = FALSE;
                }
            }
            wchar_t buf[128];
            if (ok && written == bytes) {
                StringCchPrintfW(buf, ARRAYSIZE(buf), L"pipe=sent(%lu bytes, server pid %lu)", written, serverPid);
            } else {
                StringCchPrintfW(buf, ARRAYSIZE(buf), L"pipe=timeout-or-error(server pid %lu) -> production would fall back to stock copy", serverPid);
            }
            result = buf;
            CloseHandle(ov.hEvent);
        }
        CloseHandle(pipe);
        return result;
    } catch (...) {
        return L"pipe=exception -> fallback";
    }
}

class DragDropHandler final : public IShellExtInit, public IContextMenu {
public:
    DragDropHandler() noexcept { InterlockedIncrement(&g_objects); }
    DragDropHandler(const DragDropHandler&) = delete;
    DragDropHandler& operator=(const DragDropHandler&) = delete;

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IShellExtInit) {
            *ppv = static_cast<IShellExtInit*>(this);
        } else if (riid == IID_IContextMenu) {
            *ppv = static_cast<IContextMenu*>(this);
        } else {
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() noexcept override { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }
    IFACEMETHODIMP_(ULONG) Release() noexcept override {
        LONG r = InterlockedDecrement(&refs_);
        if (r == 0) delete this;
        return static_cast<ULONG>(r);
    }

    // IShellExtInit: pidlFolder is the drop TARGET for a DragDropHandler; pdtobj holds the dragged items.
    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE pidlFolder, IDataObject* pdtobj, HKEY) noexcept override {
        try {
            target_.clear();
            sources_.clear();
            if (!pidlFolder || !pdtobj) return E_INVALIDARG;

            // Only real file-system folders. A .zip is a "folder" with a file-system path too, so also
            // require that it is not a stream (SFGAO_STREAM marks zip/cab files shown as folders).
            IShellItem* targetItem = nullptr;
            if (FAILED(SHCreateItemFromIDList(pidlFolder, IID_PPV_ARGS(&targetItem)))) return E_FAIL;
            SFGAOF attrs = 0;
            HRESULT hrAttr = targetItem->GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER | SFGAO_STREAM, &attrs);
            targetItem->Release();
            if (FAILED(hrAttr) || (attrs & (SFGAO_FILESYSTEM | SFGAO_FOLDER)) != (SFGAO_FILESYSTEM | SFGAO_FOLDER) ||
                (attrs & SFGAO_STREAM)) {
                return E_FAIL;
            }

            PWSTR path = nullptr;
            if (FAILED(SHGetNameFromIDList(pidlFolder, SIGDN_FILESYSPATH, &path)) || !path) {
                return E_FAIL;  // not a normal folder (This PC, phone, zip...): add nothing
            }
            target_ = path;
            CoTaskMemFree(path);

            FORMATETC fe = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
            STGMEDIUM stg = {};
            if (FAILED(pdtobj->GetData(&fe, &stg))) return E_FAIL;
            HDROP drop = static_cast<HDROP>(GlobalLock(stg.hGlobal));
            if (drop) {
                UINT count = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
                for (UINT i = 0; i < count && sources_.size() < kMaxItems; ++i) {
                    UINT len = DragQueryFileW(drop, i, nullptr, 0);
                    std::wstring item(static_cast<size_t>(len) + 1, L'\0');
                    DragQueryFileW(drop, i, &item[0], len + 1);
                    item.resize(len);
                    sources_.push_back(std::move(item));
                }
                GlobalUnlock(stg.hGlobal);
            }
            ReleaseStgMedium(&stg);
            return sources_.empty() ? E_FAIL : S_OK;
        } catch (...) {
            target_.clear();
            sources_.clear();
            return E_OUTOFMEMORY;
        }
    }

    // IContextMenu
    IFACEMETHODIMP QueryContextMenu(HMENU menu, UINT index, UINT first, UINT last, UINT flags) noexcept override {
        try {
            if ((flags & CMF_DEFAULTONLY) || target_.empty() || sources_.empty()) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
            if (first + kCmdMove > last) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);

            InsertMenuW(menu, index, MF_BYPOSITION | MF_STRING, first + kCmdCopy, L"Copy here with Ferry (test)");
            InsertMenuW(menu, index + 1, MF_BYPOSITION | MF_STRING, first + kCmdMove, L"Move here with Ferry (test)");

            LONG n = InterlockedIncrement(&g_menuCount);
            wchar_t buf[96];
            StringCchPrintfW(buf, ARRAYSIZE(buf), L"event=menu n=%ld items=%zu", n, sources_.size());
            LogLine(buf);
            return MAKE_HRESULT(SEVERITY_SUCCESS, 0, kCmdCount);
        } catch (...) {
            return E_FAIL;
        }
    }

    IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* info) noexcept override {
        try {
            if (!info || HIWORD(info->lpVerb) != 0) return E_INVALIDARG;  // no string verbs
            UINT cmd = LOWORD(info->lpVerb);
            if (cmd != kCmdCopy && cmd != kCmdMove) return E_INVALIDARG;
            const wchar_t* op = cmd == kCmdMove ? L"move" : L"copy";

            std::wstring msg = L"FERRYSPIKE1\nop=";
            msg += op;
            msg += L"\ntarget=" + target_ + L"\ncount=" + std::to_wstring(sources_.size()) + L"\n";
            for (const auto& s : sources_) msg += L"source=" + s + L"\n";

            ULONGLONG t0 = GetTickCount64();
            std::wstring status = SendToPipe(msg);
            ULONGLONG ms = GetTickCount64() - t0;

            LONG n = InterlockedIncrement(&g_invokeCount);
            wchar_t head[96];
            StringCchPrintfW(head, ARRAYSIZE(head), L"event=invoke n=%ld op=%s items=%zu ms=%llu ", n, op, sources_.size(), ms);
            LogLine(head + status);
            return S_OK;  // spike: nothing else happens; production returns only after the helper accepted
        } catch (...) {
            return E_FAIL;
        }
    }

    IFACEMETHODIMP GetCommandString(UINT_PTR cmd, UINT type, UINT*, CHAR* name, UINT cch) noexcept override {
        if (cmd >= kCmdCount || !name || cch == 0) return E_INVALIDARG;
        if (type == GCS_HELPTEXTW) {
            return StringCchCopyW(reinterpret_cast<wchar_t*>(name), cch,
                                  cmd == kCmdMove ? L"Move with Ferry (test only, nothing is moved)" : L"Copy with Ferry (test only, nothing is copied)");
        }
        if (type == GCS_VERBW) {
            return StringCchCopyW(reinterpret_cast<wchar_t*>(name), cch, cmd == kCmdMove ? L"ferryspikemove" : L"ferryspikecopy");
        }
        return E_INVALIDARG;
    }

private:
    ~DragDropHandler() noexcept { InterlockedDecrement(&g_objects); }

    volatile LONG refs_ = 1;
    std::wstring target_;
    std::vector<std::wstring> sources_;
};

class ClassFactory final : public IClassFactory {
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    // The factory object is static, but every reference handed out counts towards DllCanUnloadNow,
    // so COM never unloads the DLL while a caller still holds the factory.
    IFACEMETHODIMP_(ULONG) AddRef() noexcept override { return static_cast<ULONG>(InterlockedIncrement(&g_factoryRefs)); }
    IFACEMETHODIMP_(ULONG) Release() noexcept override { return static_cast<ULONG>(InterlockedDecrement(&g_factoryRefs)); }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) noexcept override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        DragDropHandler* h = new (std::nothrow) DragDropHandler();
        if (!h) return E_OUTOFMEMORY;
        HRESULT hr = h->QueryInterface(riid, ppv);
        h->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) noexcept override {
        if (lock) InterlockedIncrement(&g_locks); else InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

ClassFactory g_factory;

}  // namespace

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (clsid != CLSID_FerrySpikeDragDrop) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow() {
    return (g_objects == 0 && g_locks == 0 && g_factoryRefs == 0) ? S_OK : S_FALSE;
}
