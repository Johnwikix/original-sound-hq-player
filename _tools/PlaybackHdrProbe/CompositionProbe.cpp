// Isolated native host for the acrylic / scRGB / XAML island composition experiment.
#include <windows.h>
#include <dcomp.h>
#include <d3d11_1.h>
#include <dxgi1_6.h>
#include <dwmapi.h>
#include <wrl/client.h>
#include <new>
#include <memory>
#include <vector>
using Microsoft::WRL::ComPtr;

struct Probe {
    HWND hwnd{};
    ComPtr<IDXGISwapChain1> chain;
    ComPtr<IDCompositionDevice> composition;
    ComPtr<IDCompositionTarget> target;
    ComPtr<IDCompositionVisual> visual;
    ~Probe() {
        if (target) { target->SetRoot(nullptr); composition->Commit(); }
        if (hwnd) DestroyWindow(hwnd);
    }
};
#define CHECK(expr) do { HRESULT value = (expr); if (FAILED(value)) return value; } while (0)

extern "C" __declspec(dllexport) HRESULT OpenProbe(Probe** result, HWND* window, int* advancedColor) {
    *result = nullptr;
    *window = nullptr;
    *advancedColor = 0;
    auto p = std::unique_ptr<Probe>(new(std::nothrow) Probe());
    if (!p) return E_OUTOFMEMORY;
    p->hwnd = CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW, L"STATIC", L"Playback HDR composition probe",
        WS_POPUP, 80, 80, 450, 180, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!p->hwnd) return HRESULT_FROM_WIN32(GetLastError());
    MARGINS glass{-1, -1, -1, -1};
    CHECK(DwmExtendFrameIntoClientArea(p->hwnd, &glass));
    // Windows 11 transient system backdrop is desktop acrylic. Report unsupported OS configurations.
    DWM_SYSTEMBACKDROP_TYPE material = DWMSBT_TRANSIENTWINDOW;
    CHECK(DwmSetWindowAttribute(p->hwnd, DWMWA_SYSTEMBACKDROP_TYPE, &material, sizeof(material)));
    ComPtr<ID3D11Device> d3d;
    ComPtr<ID3D11DeviceContext> context;
    CHECK(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, &context));
    ComPtr<IDXGIDevice> device;
    CHECK(d3d.As(&device));
    ComPtr<IDXGIAdapter> adapter;
    CHECK(device->GetAdapter(&adapter));
    ComPtr<IDXGIFactory2> factory;
    CHECK(adapter->GetParent(IID_PPV_ARGS(&factory)));
    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = 450; desc.Height = 180; desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
    desc.SampleDesc.Count = 1; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2;
    desc.Scaling = DXGI_SCALING_STRETCH; desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
    CHECK(factory->CreateSwapChainForComposition(d3d.Get(), &desc, nullptr, &p->chain));
    ComPtr<IDXGISwapChain3> chain3;
    CHECK(p->chain.As(&chain3));
    UINT support{};
    auto colorSpace = DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709;
    CHECK(chain3->CheckColorSpaceSupport(colorSpace, &support));
    if (!(support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT)) return DXGI_ERROR_UNSUPPORTED;
    CHECK(chain3->SetColorSpace1(colorSpace));
    CHECK(DCompositionCreateDevice(device.Get(), IID_PPV_ARGS(&p->composition)));
    CHECK(p->composition->CreateTargetForHwnd(p->hwnd, FALSE, &p->target));
    CHECK(p->composition->CreateVisual(&p->visual));
    CHECK(p->visual->SetContent(p->chain.Get()));
    CHECK(p->target->SetRoot(p->visual.Get()));
    CHECK(p->composition->Commit());
    ComPtr<ID3D11Texture2D> back;
    CHECK(p->chain->GetBuffer(0, IID_PPV_ARGS(&back)));
    ComPtr<ID3D11RenderTargetView> view;
    CHECK(d3d->CreateRenderTargetView(back.Get(), nullptr, &view));
    // Use scissored ClearView (D3D11.1) to draw neutral reference bars with known alpha.
    ComPtr<ID3D11DeviceContext1> ctx;
    CHECK(context.As(&ctx));
    const float transparent[] = {0, 0, 0, 0};
    context->ClearRenderTargetView(view.Get(), transparent);
    const float nits[] = {200, 400, 600, 600};
    for (int i = 0; i < 4; ++i) {
        float alpha = i == 3 ? 0.5f : 1.0f;
        float value = nits[i] / 80.0f * alpha;
        float color[] = {value, value, value, alpha};
        D3D11_RECT area{90 + 80*i, 35, 160 + 80*i, 145};
        ctx->ClearView(view.Get(), color, &area, 1);
    }
    CHECK(p->chain->Present(1, 0));
    HMONITOR monitor = MonitorFromWindow(p->hwnd, MONITOR_DEFAULTTONEAREST);
    for (UINT index = 0; ; ++index) {
        ComPtr<IDXGIOutput> output;
        if (FAILED(adapter->EnumOutputs(index, &output))) break;
        ComPtr<IDXGIOutput6> output6;
        if (FAILED(output.As(&output6))) continue;
        DXGI_OUTPUT_DESC1 info{};
        if (SUCCEEDED(output6->GetDesc1(&info)) && info.Monitor == monitor)
            *advancedColor = info.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
    }
    *window = p->hwnd;
    *result = p.release();
    return S_OK;
}

extern "C" __declspec(dllexport) void CloseProbe(Probe* probe) { delete probe; }

// Only copy the requested pixels inside our test windows to the CPU. The desktop frame is never saved.
#include <DirectXPackedVector.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

static LRESULT CALLBACK BackdropProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l) {
    if (msg == WM_PAINT) {
        PAINTSTRUCT ps{};
        HDC dc = BeginPaint(hwnd, &ps);
        HBRUSH brush = CreateSolidBrush(static_cast<COLORREF>(GetWindowLongPtrW(hwnd, GWLP_USERDATA)));
        FillRect(dc, &ps.rcPaint, brush);
        DeleteObject(brush);
        EndPaint(hwnd, &ps);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, w, l);
}

extern "C" __declspec(dllexport) HWND ShowBackdrop(HWND hdrHost) {
    WNDCLASSW cls{};
    cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpfnWndProc = BackdropProc;
    cls.lpszClassName = L"MusicHdrTestBackdrop";
    RegisterClassW(&cls);
    RECT bounds{};
    GetWindowRect(hdrHost, &bounds);
    HWND behind = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, cls.lpszClassName, L"HDR test pattern",
        WS_POPUP, bounds.left - 16, bounds.top - 16, bounds.right - bounds.left + 32,
        bounds.bottom - bounds.top + 32, nullptr, nullptr, cls.hInstance, nullptr);
    if (!behind) return nullptr;
    SetWindowLongPtrW(behind, GWLP_USERDATA, RGB(240, 32, 24));
    SetWindowPos(hdrHost, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    SetForegroundWindow(hdrHost);
    SetActiveWindow(hdrHost);
    SetWindowPos(behind, hdrHost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    DwmFlush();
    return behind;
}

extern "C" __declspec(dllexport) void ChangeBackdrop(HWND behind) {
    SetWindowLongPtrW(behind, GWLP_USERDATA, RGB(24, 32, 240));
    InvalidateRect(behind, nullptr, TRUE);
    UpdateWindow(behind);
    DwmFlush();
}

static HRESULT ReadFrame(HWND host, bool windowOnly, const POINT* points, int count, float* rgb,
    INT64* times = nullptr, int maximumFrames = 1, int* frames = nullptr,
    HANDLE ready = nullptr, HANDLE completed = nullptr) {
    if (count <= 0 || count > 1024 || maximumFrames <= 0) return E_INVALIDARG;
    ComPtr<IDXGIFactory1> factory;
    CHECK(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
    HMONITOR monitor = MonitorFromWindow(host, MONITOR_DEFAULTTONEAREST);
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIOutput5> output;
    DXGI_OUTPUT_DESC desc{};
    for (UINT a = 0; !output; ++a) {
        CHECK(factory->EnumAdapters1(a, adapter.ReleaseAndGetAddressOf()));
        for (UINT o = 0; ; ++o) {
            ComPtr<IDXGIOutput> candidate;
            if (FAILED(adapter->EnumOutputs(o, &candidate))) break;
            if (SUCCEEDED(candidate->GetDesc(&desc)) && desc.Monitor == monitor) {
                CHECK(candidate.As(&output));
                break;
            }
        }
    }
    // Graphics Capture returns the final display as a floating-point scRGB surface in HDR mode.
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> ctx;
    CHECK(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &ctx));
    using namespace winrt::Windows::Graphics::Capture;
    using namespace winrt::Windows::Graphics::DirectX;
    ComPtr<IDXGIDevice> dxgi;
    CHECK(device.As(&dxgi));
    winrt::com_ptr<::IInspectable> inspectable;
    CHECK(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), inspectable.put()));
    auto winrtDevice = inspectable.as<Direct3D11::IDirect3DDevice>();
    auto interop = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    GraphicsCaptureItem item{ nullptr };
    if (windowOnly) {
        CHECK(interop->CreateForWindow(host, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item)));
    } else {
        CHECK(interop->CreateForMonitor(monitor, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item)));
    }
    auto pool = Direct3D11CaptureFramePool::CreateFreeThreaded(winrtDevice, DirectXPixelFormat::R16G16B16A16Float, 2, item.Size());
    auto session = pool.CreateCaptureSession(item);
    session.IsCursorCaptureEnabled(false);
    session.StartCapture();
    Direct3D11CaptureFrame frame{ nullptr };
    for (int i = 0; i < 100 && !frame; ++i) {
        Sleep(25);
        frame = pool.TryGetNextFrame();
    }
    if (!frame) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
    ComPtr<ID3D11Texture2D> capture;
    CHECK(access->GetInterface(IID_PPV_ARGS(&capture)));
    constexpr auto format = DXGI_FORMAT_R16G16B16A16_FLOAT;
    D3D11_TEXTURE2D_DESC source{};
    capture->GetDesc(&source);
    if (source.Format != format) return DXGI_ERROR_UNSUPPORTED;
    RECT win{};
    GetWindowRect(host, &win);
    if (!times) printf("%s frame %ux%u desktop (%ld,%ld)-(%ld,%ld), probe (%ld,%ld)-(%ld,%ld), DPI %u\n",
        windowOnly ? "Window" : "Monitor", source.Width, source.Height, desc.DesktopCoordinates.left, desc.DesktopCoordinates.top,
        desc.DesktopCoordinates.right, desc.DesktopCoordinates.bottom, win.left, win.top, win.right, win.bottom, GetDpiForWindow(host));
    RECT origin = desc.DesktopCoordinates;
    if (windowOnly) {
        origin = win;
        RECT frameBounds{};
        if (SUCCEEDED(DwmGetWindowAttribute(host, DWMWA_EXTENDED_FRAME_BOUNDS, &frameBounds, sizeof frameBounds))
            && frameBounds.right - frameBounds.left == static_cast<LONG>(source.Width)) origin = frameBounds;
    }
    D3D11_TEXTURE2D_DESC staging{};
    staging.Width = count; staging.Height = 1; staging.MipLevels = 1; staging.ArraySize = 1;
    staging.Format = format; staging.SampleDesc.Count = 1; staging.Usage = D3D11_USAGE_STAGING;
    staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> sample;
    CHECK(device->CreateTexture2D(&staging, nullptr, &sample));
    RECT client{};
    GetClientRect(host, &client);
    std::vector<D3D11_BOX> boxes(count);
    for (int i = 0; i < count; ++i) {
        POINT point = points[i];
        if (!PtInRect(&client, point) || !ClientToScreen(host, &point)) return E_INVALIDARG;
        POINT screen = point;
        point.x -= origin.left;
        point.y -= origin.top;
        if (point.x < 0 || point.y < 0 || static_cast<UINT>(point.x) >= source.Width ||
            static_cast<UINT>(point.y) >= source.Height) return E_INVALIDARG;
        HWND at = windowOnly ? host : WindowFromPoint(screen);
        if (at != host && !IsChild(host, at) && GetAncestor(at, GA_ROOTOWNER) != host) {
            wchar_t covering[128]{};
            if (at) GetClassNameW(at, covering, ARRAYSIZE(covering));
            DWORD cloaked{};
            DwmGetWindowAttribute(host, DWMWA_CLOAKED, &cloaked, sizeof cloaked);
            printf("Sample blocked at (%ld,%ld), covering window class=%ls, host visible=%d, iconic=%d, cloaked=%lu\n",
                screen.x, screen.y, at ? covering : L"(none)", IsWindowVisible(host), IsIconic(host), cloaked);
            return HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE);
        }
        boxes[i] = {static_cast<UINT>(point.x), static_cast<UINT>(point.y), 0,
            static_cast<UINT>(point.x + 1), static_cast<UINT>(point.y + 1), 1};
    }
    int captured = 0;
    ULONGLONG stop = GetTickCount64() + (completed ? 10000 : 650);
    while (frame && captured < maximumFrames) {
        for (int i = 0; i < count; ++i)
            ctx->CopySubresourceRegion(sample.Get(), 0, i, 0, 0, capture.Get(), 0, &boxes[i]);
        D3D11_MAPPED_SUBRESOURCE data{};
        CHECK(ctx->Map(sample.Get(), 0, D3D11_MAP_READ, 0, &data));
        auto pixels = static_cast<const uint16_t*>(data.pData);
        for (int i = 0; i < count; ++i)
            for (int c = 0; c < 3; ++c) rgb[3*i+c] = DirectX::PackedVector::XMConvertHalfToFloat(pixels[4*i+c]);
        ctx->Unmap(sample.Get(), 0);
        if (times) times[captured] = frame.SystemRelativeTime().count();
        rgb += count * 3;
        ++captured;
        if (ready && captured == 1) SetEvent(ready);
        frame.Close();
        frame = nullptr;
        capture.Reset();
        access = nullptr;
        if (captured == maximumFrames || (completed && WaitForSingleObject(completed, 0) == WAIT_OBJECT_0)) break;
        while (!frame && GetTickCount64() < stop) {
            if (completed) {
                if (WaitForSingleObject(completed, 2) == WAIT_OBJECT_0) break;
            } else {
                Sleep(2);
            }
            frame = pool.TryGetNextFrame();
        }
        if (frame) {
            access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            CHECK(access->GetInterface(IID_PPV_ARGS(&capture)));
        }
    }
    if (frames) *frames = captured;
    session.Close();
    pool.Close();
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT SampleFrame(HWND host, BOOL windowOnly, const POINT* points, int count, float* rgb) noexcept {
    try { return ReadFrame(host, windowOnly != FALSE, points, count, rgb); }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT SampleMotionFrames(HWND host, const POINT* points, int count, float* rgb,
    INT64* times, int maximumFrames, int* frames) noexcept {
    try { return ReadFrame(host, true, points, count, rgb, times, maximumFrames, frames); }
    catch (...) { return winrt::to_hresult(); }
}


extern "C" __declspec(dllexport) HRESULT SampleNavigationFrames(HWND host, const POINT* points, int count, float* rgb,
    INT64* times, int maximumFrames, int* frames, HANDLE ready, HANDLE completed) noexcept {
    try { return ReadFrame(host, true, points, count, rgb, times, maximumFrames, frames, ready, completed); }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT QueryPlaybackProcessVideoMemory(
    UINT64* local, UINT64* nonLocal, UINT* adapters) {
    *local = 0;
    *nonLocal = 0;
    *adapters = 0;
    ComPtr<IDXGIFactory1> factory;
    CHECK(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));

    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter1> adapter;
        HRESULT hr = factory->EnumAdapters1(i, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        CHECK(hr);
        DXGI_ADAPTER_DESC1 desc{};
        CHECK(adapter->GetDesc1(&desc));
        if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;

        ComPtr<IDXGIAdapter3> budgeting;
        CHECK(adapter.As(&budgeting));
        DXGI_QUERY_VIDEO_MEMORY_INFO usage{};
        CHECK(budgeting->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &usage));
        *local += usage.CurrentUsage;
        CHECK(budgeting->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &usage));
        *nonLocal += usage.CurrentUsage;
        ++*adapters;
    }
    return *adapters > 0 ? S_OK : DXGI_ERROR_NOT_FOUND;
}
