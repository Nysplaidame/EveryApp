// Out-of-Explorer test harness for the spike DragDropHandler. Loads the DLL by path into THIS process
// (no registry, no Explorer), then does what Explorer does on a right-drag N times:
// create handler -> Initialize(target, data object) -> QueryContextMenu -> InvokeCommand -> Release.
// Uses two empty files in a new temp folder. Nothing is copied (the handler never copies).
// Usage: harness.exe <path-to-dll> [iterations=200] [delayMs=0]

#include <windows.h>
#include <shlobj.h>
#include <psapi.h>
#include <cstdio>
#include <string>

static const CLSID CLSID_FerrySpikeDragDrop = {0x4a158046, 0x8ed4, 0x476f, {0xb5, 0x8b, 0x19, 0xd7, 0xb7, 0xd7, 0x75, 0xfe}};

static int Fail(const char* what, HRESULT hr) {
    std::printf("FAIL: %s (hr=0x%08lx)\n", what, static_cast<unsigned long>(hr));
    return 1;
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) { std::printf("usage: harness.exe <dll> [iterations]\n"); return 2; }
    int iterations = argc > 2 ? _wtoi(argv[2]) : 200;
    if (iterations <= 0) iterations = 200;
    int delayMs = argc > 3 ? _wtoi(argv[3]) : 0;  // pause between drags, like a person would

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr)) return Fail("CoInitializeEx", hr);

    HMODULE dll = LoadLibraryW(argv[1]);
    if (!dll) return Fail("LoadLibrary", HRESULT_FROM_WIN32(GetLastError()));
    auto getClassObject = reinterpret_cast<HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**)>(GetProcAddress(dll, "DllGetClassObject"));
    auto canUnload = reinterpret_cast<HRESULT(STDAPICALLTYPE*)()>(GetProcAddress(dll, "DllCanUnloadNow"));
    if (!getClassObject || !canUnload) return Fail("exports missing", E_FAIL);

    // Temp folder with a source subfolder (two empty files) and a target subfolder.
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    std::wstring root = std::wstring(tmp) + L"FerrySpikeHarness-" + std::to_wstring(GetCurrentProcessId());
    std::wstring src = root + L"\\src", dst = root + L"\\dst";
    CreateDirectoryW(root.c_str(), nullptr);
    CreateDirectoryW(src.c_str(), nullptr);
    CreateDirectoryW(dst.c_str(), nullptr);
    for (const wchar_t* name : {L"\\a.txt", L"\\b.txt"}) {
        HANDLE h = CreateFileW((src + name).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    }

    PIDLIST_ABSOLUTE dstPidl = nullptr, srcPidl = nullptr;
    if (FAILED(hr = SHParseDisplayName(dst.c_str(), nullptr, &dstPidl, 0, nullptr))) return Fail("parse dst", hr);
    if (FAILED(hr = SHParseDisplayName(src.c_str(), nullptr, &srcPidl, 0, nullptr))) return Fail("parse src", hr);

    IShellFolder* srcFolder = nullptr;
    if (FAILED(hr = SHBindToObject(nullptr, srcPidl, nullptr, IID_PPV_ARGS(&srcFolder)))) return Fail("bind src", hr);
    PITEMID_CHILD kids[2] = {};
    const wchar_t* names[2] = {L"a.txt", L"b.txt"};
    for (int i = 0; i < 2; ++i) {
        if (FAILED(hr = srcFolder->ParseDisplayName(nullptr, nullptr, const_cast<LPWSTR>(names[i]), nullptr,
                                                    reinterpret_cast<PIDLIST_RELATIVE*>(&kids[i]), nullptr))) return Fail("parse child", hr);
    }
    IDataObject* data = nullptr;
    if (FAILED(hr = srcFolder->GetUIObjectOf(nullptr, 2, const_cast<PCUITEMID_CHILD_ARRAY>(kids), IID_IDataObject, nullptr,
                                             reinterpret_cast<void**>(&data)))) return Fail("GetUIObjectOf(IDataObject)", hr);

    IClassFactory* factory = nullptr;
    if (FAILED(hr = getClassObject(CLSID_FerrySpikeDragDrop, IID_PPV_ARGS(&factory)))) return Fail("DllGetClassObject", hr);

    PROCESS_MEMORY_COUNTERS before = {sizeof(before)}, after = {sizeof(after)};
    DWORD handlesBefore = 0, handlesAfter = 0;
    GetProcessMemoryInfo(GetCurrentProcess(), &before, sizeof(before));
    GetProcessHandleCount(GetCurrentProcess(), &handlesBefore);

    int menuOk = 0, invokeOk = 0;
    ULONGLONG maxInvokeMs = 0, t0 = GetTickCount64();
    for (int i = 0; i < iterations; ++i) {
        IShellExtInit* init = nullptr;
        if (FAILED(hr = factory->CreateInstance(nullptr, IID_PPV_ARGS(&init)))) return Fail("CreateInstance", hr);
        if (FAILED(hr = init->Initialize(dstPidl, data, nullptr))) return Fail("Initialize", hr);
        IContextMenu* cm = nullptr;
        if (FAILED(hr = init->QueryInterface(IID_PPV_ARGS(&cm)))) return Fail("QI IContextMenu", hr);
        HMENU menu = CreatePopupMenu();
        hr = cm->QueryContextMenu(menu, 0, 1, 0x7FFF, CMF_NORMAL);
        if (SUCCEEDED(hr) && HRESULT_CODE(hr) == 2 && GetMenuItemCount(menu) == 2) ++menuOk;
        CMINVOKECOMMANDINFO ici = {sizeof(ici)};
        ici.lpVerb = MAKEINTRESOURCEA(i % 2);  // alternate copy / move
        ULONGLONG s = GetTickCount64();
        if (SUCCEEDED(cm->InvokeCommand(&ici))) ++invokeOk;
        ULONGLONG ms = GetTickCount64() - s;
        if (ms > maxInvokeMs) maxInvokeMs = ms;
        DestroyMenu(menu);
        cm->Release();
        init->Release();
        if (delayMs > 0) Sleep(static_cast<DWORD>(delayMs));
    }
    ULONGLONG totalMs = GetTickCount64() - t0;

    GetProcessMemoryInfo(GetCurrentProcess(), &after, sizeof(after));
    GetProcessHandleCount(GetCurrentProcess(), &handlesAfter);
    HRESULT unload = canUnload();

    // Negative checks: bad verb and non-file-system target must be refused cleanly.
    IShellExtInit* init = nullptr;
    factory->CreateInstance(nullptr, IID_PPV_ARGS(&init));
    IContextMenu* cm = nullptr;
    init->Initialize(dstPidl, data, nullptr);
    init->QueryInterface(IID_PPV_ARGS(&cm));
    CMINVOKECOMMANDINFO bad = {sizeof(bad)};
    bad.lpVerb = "delete";
    HRESULT badVerb = cm->InvokeCommand(&bad);
    cm->Release();
    PIDLIST_ABSOLUTE thisPc = nullptr;
    SHGetKnownFolderIDList(FOLDERID_ComputerFolder, 0, nullptr, &thisPc);
    HRESULT virtualTarget = init->Initialize(thisPc, data, nullptr);
    CoTaskMemFree(thisPc);

    // A .zip shown as a folder must be refused too. Minimal valid empty zip = end-of-central-directory record.
    std::wstring zip = root + L"\\test.zip";
    {
        const unsigned char eocd[22] = {'P', 'K', 5, 6};
        HANDLE h = CreateFileW(zip.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        DWORD w = 0;
        if (h != INVALID_HANDLE_VALUE) { WriteFile(h, eocd, sizeof(eocd), &w, nullptr); CloseHandle(h); }
    }
    PIDLIST_ABSOLUTE zipPidl = nullptr;
    HRESULT zipTarget = E_FAIL;
    if (SUCCEEDED(SHParseDisplayName(zip.c_str(), nullptr, &zipPidl, 0, nullptr))) {
        zipTarget = init->Initialize(zipPidl, data, nullptr);
        CoTaskMemFree(zipPidl);
    } else {
        std::printf("note: could not parse test.zip, zip check skipped\n");
    }
    init->Release();
    DeleteFileW(zip.c_str());

    std::printf("iterations=%d menuOk=%d invokeOk=%d totalMs=%llu maxInvokeMs=%llu\n", iterations, menuOk, invokeOk, totalMs, maxInvokeMs);
    std::printf("workingSetDeltaKB=%lld handleDelta=%ld\n",
                (static_cast<long long>(after.WorkingSetSize) - static_cast<long long>(before.WorkingSetSize)) / 1024,
                static_cast<long>(handlesAfter) - static_cast<long>(handlesBefore));
    std::printf("badVerbRefused=%s virtualTargetRefused=%s zipTargetRefused=%s\n", FAILED(badVerb) ? "yes" : "NO",
                FAILED(virtualTarget) ? "yes" : "NO", FAILED(zipTarget) ? "yes" : "NO");

    // Unload rules: while we hold the factory the DLL must NOT be unloadable; after releasing it, it must be.
    HRESULT unloadWhileHeld = unload;
    factory->Release();
    HRESULT unloadAfter = canUnload();
    std::printf("DllCanUnloadNow: factory held=%s (want S_FALSE), factory released=%s (want S_OK)\n",
                unloadWhileHeld == S_OK ? "S_OK" : "S_FALSE", unloadAfter == S_OK ? "S_OK" : "S_FALSE");
    data->Release();
    for (auto k : kids) CoTaskMemFree(k);
    srcFolder->Release();
    CoTaskMemFree(srcPidl);
    CoTaskMemFree(dstPidl);
    DeleteFileW((src + L"\\a.txt").c_str());
    DeleteFileW((src + L"\\b.txt").c_str());
    RemoveDirectoryW(src.c_str());
    RemoveDirectoryW(dst.c_str());
    RemoveDirectoryW(root.c_str());
    CoUninitialize();
    bool pass = menuOk == iterations && invokeOk == iterations && unloadWhileHeld == S_FALSE && unloadAfter == S_OK &&
                FAILED(badVerb) && FAILED(virtualTarget) && FAILED(zipTarget) && (maxInvokeMs < 350);
    std::printf("maxInvokeUnder350ms=%s\n", maxInvokeMs < 350 ? "yes" : "NO");
    std::printf("%s\n", pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}
