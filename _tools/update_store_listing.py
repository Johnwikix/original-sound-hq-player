# -*- coding: utf-8 -*-
"""Update the Microsoft Store listing CSV for v1.2.3.0 (paid transition + refreshed listing).

Idempotent: all text fields are replaced wholesale, so re-running is safe.
"""
import csv

CSV_PATH = r"H:\download\listingData-9NFW1RPPT999-1152921505701887236.csv"
NOTES_LIMIT = 1500      # Partner Center ReleaseNotes limit
DESC_LIMIT = 10000      # Description limit
SHORT_LIMIT = 500       # ShortDescription limit

# ---------------------------------------------------------------- ReleaseNotes

ZH_NOTES = """v1.2.3.0
• 本版本起转为收费应用：免费试用 + 一次性买断（Microsoft Store），试用期内全功能开放；到期后需完整版解锁卷积校正、DSD 位流输出（DoP/Native）、5.1 输出与 Atmos 直通，基础播放、均衡器及其余音效保持免费，购买后原设置自动恢复
• 新增首次启动用户协议与隐私政策确认
• 卷积校正支持按输出设备绑定，插拔或切换输出设备时自动应用对应校正，未绑定设备自动旁路
• 卷积校正编辑改为即时生效并自动保存，支持无音频输出时离线编辑草稿
• 新增音乐库与收藏页空状态引导
• 启动不再等待文件扫描：主界面即开即用，增量扫描转入后台并显示进度，不打断当前播放
• 重构添加文件链路：歌曲分批渐进上架，扫描期间自动互斥重复操作
• 标签编辑延迟写入：文件被占用时不再报错，可写后自动补写，跨重启保留
• 目录监视关闭时彻底释放、重新开启即时恢复，多次变更合并为一次扫描
• 新增「播放详情页在前台时隐藏桌面歌词」选项，桌面歌词不再抢占焦点
• 歌词：无歌词时显示友好提示；修复网络异常被误判为无歌词的问题，联网后自动补搜
• 修复无封面歌曲切换时旧封面与主题色残留、默认封面不随主题、取色错误
• 修复快速连点播放/暂停丢操作的问题
• 卷积与输出切换更即时顺滑；音频设置原子保存，损坏自动恢复"""

EN_NOTES = """v1.2.3.0
• Now paid: free trial + one-time full-version purchase (Microsoft Store). All features during the trial; afterwards the full version unlocks convolution correction, DSD bitstream (DoP/Native), 5.1 output and Atmos passthrough. Playback, the equalizer and other effects stay free; settings are restored after purchase
• First-run user agreement & privacy policy consent
• Convolution correction can be bound per output device, applied automatically on plug/unplug or switching
• Convolution editing is instant with auto-save; offline editing supported without audio output
• Empty-state guidance for the library and favorites
• Startup no longer waits for scanning: the UI is ready instantly; background scanning with progress never interrupts playback
• Reworked add-files: progressive listing with operations guarded during scans
• Lazy tag writes: no errors on busy files; written automatically once writable
• Folder watching stops cleanly when disabled and resumes instantly
• New option to hide desktop lyrics while the now-playing page is foreground
• Lyrics: friendly no-lyrics placeholder; network errors no longer block future searches
• Fixed stale covers/theme colors on tracks without cover art; default cover follows theme
• Fixed dropped rapid play/pause clicks
• Snappier convolution/output switching; atomic audio settings"""

ES_NOTES = """v1.2.3.0
• Ahora es de pago: prueba gratuita + compra única (Microsoft Store). Todo disponible durante la prueba; después, la versión completa desbloquea la corrección por convolución, DSD (DoP/Native), 5.1 y Atmos. La reproducción, el ecualizador y el resto de efectos siguen gratis
• Acuerdo de usuario y política de privacidad al primer inicio
• Convolución vinculable por dispositivo de salida, aplicada al cambiar de dispositivo
• Edición de convolución instantánea con guardado automático; editable sin salida de audio
• Guías para la biblioteca y los favoritos vacíos
• Inicio sin esperar el escaneo: la interfaz abre al instante; el escaneo en segundo plano no interrumpe la reproducción
• Adición de archivos renovada: listado progresivo con operaciones protegidas durante el escaneo
• Etiquetas: escritura diferida, sin errores con archivos ocupados
• La vigilancia de carpetas se desactiva por completo y se reactiva al instante
• Nueva opción para ocultar la letra de escritorio con la página de reproducción en primer plano
• Letras: aviso amable sin letra; los errores de red ya no bloquean las búsquedas
• Portadas y colores obsoletos corregidos en pistas sin portada; portada por defecto según el tema
• Los clics rápidos de reproducir/pausa ya no se pierden
• Cambio de convolución/salida más ágil; ajustes de audio atómicos"""

# ---------------------------------------------------------------- Description

ZH_DESC = """原音HQ播放器- 原音臻享，聆听无界
一、产品概述
原音HQ播放器是一款致力于为音乐爱好者呈上极致原声体验的 WinUI 应用程序。它聚焦于音频的高品质还原，凭借卓越的技术，打破常规聆听边界。应用基于 FFmpeg 解码与自研 WASAPI/ASIO 互操作的独立音频引擎，以 64 位浮点管线处理全程音频。支持包括 DSD（DSF/DFF）、FLAC、WAV、MP3、AAC、M4A、OGG/OGA、Opus、WMA、AIFF、APE、WavPack（WV）在内的 12 种以上音乐格式，可以将支持的音频转换为 WAV、FLAC、ALAC、MP3、AAC、OGG、Opus（有损格式可指定码率）。音频输出支持 WASAPI 独占/共享、DirectSound、ASIO 以及 DSD DoP/Native 输出。
【重要｜版本与购买】本应用提供免费试用，试用期内可使用全部功能（试用时长以商店页面为准）。试用结束后仍可正常播放本地音乐，基础播放、十段均衡器及其余常规音效保持免费；卷积校正、DSD 位流输出（DoP/Native）、5.1 多声道输出与 Atmos HDMI 直通需购买完整版解锁。受限期间已保存的设置不会被修改或清除，购买后自动恢复生效。首次启动需阅读并同意用户协议与隐私政策。
二、功能特性
（一）完备音乐信息
实时详情展示：播放界面实时呈现歌曲标题、创作者、专辑名、时长、采样率、码率、文件类型等核心信息，让用户对播放的音乐了如指掌，全面掌握音乐资料。
元信息匹配：根据音乐标题自动匹配歌曲专辑封面。
（二）Sony Walkman支持
可以方便的将匹配好元信息的音乐通过USB传输到你的walkman上，支持传输已经匹配好的歌词文件
（三）高效文件夹管理
手动添加扫描：用户可自主选择本地文件夹，添加到播放器中进行音乐文件扫描，将私人珍藏快速导入音乐库，扩充音乐资源版图。自动重新扫描与目录监视功能可实时更新音乐库，智能处理文件变动，如依据路径匹配更新已有记录、删除不存在文件对应的记录，确保音乐库始终保持最新、有序状态。
（四）多种输出模式
WASAPI 独占模式：支持推送和事件两种模式，此模式下，播放器独占音频设备，减少系统音频处理环节的干扰，最大程度降低音频延迟，为用户带来极低延迟、高保真的音频输出。
WASAPI 共享模式：在共享模式中，播放器与其他应用程序共享音频设备。
DirectSound 模式：DirectSound 模式具有良好的硬件加速能力，可有效提升音频播放效率，尤其在处理复杂音频场景或多声道音频时优势明显。
DSD dop: 封装为pcm帧的原始dsd输出
DSD Native：通过ASIO的原始DSD格式输出
ASIO 支持：原生 ASIO 输出，自动枚举驱动并协商缓冲与采样率，支持 ASIO 原生 DSD 扩展。
多声道与全景声：5.1 多声道 PCM 输出、Atmos（E-AC-3/JOC）HDMI 位流直通（需兼容接收设备，完整版功能）。
可靠性：输出失效看门狗自动恢复，设备热插拔与格式变更不中断播放；设备选择使用稳定端点 ID，插拔后不会错选输出设备。
（五）专业 DSP 音效
十段均衡器：各频段增益与 Q 值可调，附多种预设并支持保存。
卷积校正：绘制校正曲线（2–32 个可拖动控制点）或导入耳机/房间校正 WAV 脉冲响应（IR），生成最小相位 FIR 实时卷积；支持按输出设备绑定独立校正，插拔或切换输出设备时自动应用；编辑即时生效，内置 EQ 与卷积合成频响实时预览。
响度归一化：按 EBU R128 后台分析整曲响度，平滑应用固定增益并缓存结果，不改文件、不压缩动态。
统一前级增益：作用于均衡器与卷积，可手动设定或按合成频响峰值自动补偿，避免校正提升造成削波。
声道与耳机效果：左右平衡、声道互换、单声道合并、耳机交叉馈送、立体声宽度。
音效总开关：一键旁路全部音效。
（六）歌词与桌面歌词
滚动显示歌词：在播放页面根据歌词时间戳滚动显示，支持逐字动态动画与多种缓动模式。
自动匹配歌词：设置中可选开启歌词自动匹配（如果需要写入文件请在歌曲属性页手动操作）。
桌面歌词：桌面悬浮歌词窗口，支持卡拉 OK 模式、锁定位置与全局快捷键，可设置在播放详情页前台时自动隐藏。
（七）多元播放模式
顺序畅听：依照播放列表顺序，流畅播放每一首曲目，适合完整欣赏专辑或精心编排的歌单，引领用户稳步畅游音乐旅程。
随机邂逅：打破既定顺序，开启音乐惊喜之旅，每次点击播放都能带来意外之喜，助力用户发现曲库中的遗珠之作。
单曲循环：锁定用户喜爱的单曲，让旋律无限循环，沉浸于专属的音乐时光，尽情品味歌曲的魅力。
（八）列表管理
预设经典分类：支持按歌曲、专辑、艺术家、文件夹及收藏夹智能分类，一键即可快速定位到心仪的音乐，便捷浏览音乐库。
最爱的音乐：支持将歌曲添加至单独的最爱音频列表
自定义随心创：用户可轻松新建、编辑、删除个人专属播放列表，根据个人喜好对音乐进行个性化整理，打造符合自身音乐品味的专属集合。
（九）SMTC系统媒体传输控件集成
专辑原始封面显示，时间轴推送
（十）高度可自定义设置
可自定义的应用样式：包含云母、亚克力、透明亚克力、自定义亚克力（透明度可调）四种样式
可自定义的应用主题：包含系统默认、深色、浅色三种主题
（十一）全局快捷键
内置 13 个全局快捷键，均可在设置中自定义录制：播放/暂停（Ctrl+Alt+P）、下一首（Ctrl+Alt+→）、上一首（Ctrl+Alt+←）、音量增减（Ctrl+Alt+↑/↓）、切换播放详情（Ctrl+Alt+Q）、返回（Ctrl+Alt+B）、显示/隐藏主窗口（Ctrl+Alt+W）、切换全屏（Ctrl+Alt+F），以及桌面歌词显示/隐藏（Ctrl+Alt+D）、锁定/解锁（Ctrl+Alt+L）、逐字卡拉 OK（Ctrl+Alt+K）、重置窗口（Ctrl+Alt+R）
三、安装与使用
（一）系统要求
适配 Windows 10 19041 及以上版本操作系统（推荐 Windows 11），充分利用系统性能优势，确保音乐播放流畅稳定，为用户提供优质的聆听环境。
（二）首次使用
启动应用后，用户需手动添加包含音乐文件的文件夹，播放器将对所选文件夹进行扫描，导入音乐至本地库，构建个人音乐集合。
通过侧边栏的便捷导航，用户可快速切换至不同的播放列表页面，开始探索丰富的音乐资源，开启原声音乐之旅。
若要添加更多音乐，点击 “添加文件夹” 按钮，选择相应文件夹进行扫描导入，不断丰富个人音乐库。在播放界面，用户可根据需求选择合适的音频输出模式（WASAPI 独占、WASAPI 共享、DirectSound）以及对应的输出设备（如内置扬声器、耳机、蓝牙音箱等），以获得符合自身场景和音质需求的播放体验。
四、GitHub
主程序（含独立音频引擎 AudioPlayer）：https://github.com/Johnwikix/original-sound-hq-player
五、问题反馈
问题反馈交流QQ群：一群 1009034363，二群 1033738779"""

EN_DESC = """Original Sound HQ Player - Authentic Sound, Boundless Listening
1. Product Overview
Original Sound HQ Player is a WinUI application dedicated to providing music enthusiasts with an ultimate original sound experience. It focuses on high-quality audio reproduction and breaks the boundaries of conventional listening through excellent technology. The app runs on a standalone audio engine with FFmpeg decoding and self-developed WASAPI/ASIO interop, processing the whole pipeline in 64-bit floating point. It supports 12+ music formats including DSD (DSF/DFF), FLAC, WAV, MP3, AAC, M4A, OGG/OGA, Opus, WMA, AIFF, APE, and WavPack (WV), and can convert supported audio to WAV, FLAC, ALAC, MP3, AAC, OGG, and Opus (lossy formats with selectable bitrate). Audio output supports WASAPI exclusive/shared, DirectSound, ASIO, and DSD DoP/Native.
Important — trial & purchase: The app offers a free trial with every feature available (trial length as shown on the Store page). After the trial, local playback keeps working — core playback, the 10-band equalizer and other standard effects stay free, while convolution correction, DSD bitstream output (DoP/Native), 5.1 multichannel output and Atmos HDMI passthrough require the full-version purchase. While restricted, saved settings are never modified or erased and are restored automatically after purchase. The first launch asks you to accept the built-in user agreement and privacy policy.
2. Functional Features
(1) Comprehensive Music Information
Real-time detail display: the playback interface shows the song title, artist, album, duration, sampling rate, bitrate, and file type in real time.
Metadata matching: album covers are matched automatically by music title.
(2) Sony Walkman Support
Transfer music with matched metadata to your Walkman over USB, including lyric files that have already been matched.
(3) Efficient Folder Management
Manual add & scan: pick local folders to scan and quickly import your collection into the library. Automatic rescans and folder watching keep the library up to date — existing records are updated by path and records of missing files are removed, so the library always stays fresh and organized.
(4) Multiple Output Modes
WASAPI exclusive mode: push and event driven modes — the player takes exclusive control of the audio device, minimizing system interference and latency for high-fidelity output.
WASAPI shared mode: the player shares the audio device with other applications.
DirectSound mode: good hardware acceleration, especially effective for complex audio scenes or multichannel audio.
DSD DoP: raw DSD output encapsulated into PCM frames.
DSD Native: raw DSD output via ASIO.
ASIO support: native ASIO output with automatic driver enumeration and buffer/rate negotiation, including ASIO native DSD extensions.
Multichannel & immersive: 5.1 multichannel PCM output and Atmos (E-AC-3/JOC) HDMI bitstream passthrough (requires a compatible receiver; full version).
Reliability: an output watchdog recovers automatically — device hotplug and format changes never interrupt playback; devices are selected by stable endpoint IDs, so a replug never picks the wrong output.
(5) Professional DSP Effects
10-band equalizer: per-band gain and Q, with multiple savable presets.
Convolution correction: draw a correction curve (2–32 draggable control points) or import a headphone/room-correction WAV impulse response (IR) rendered into a minimum-phase FIR for real-time convolution; correction can be bound per output device and applied automatically on plug/unplug or switching; edits take effect instantly, with a live combined EQ/convolution response preview.
Loudness normalization: background EBU R128 analysis per track applies a smooth fixed gain and caches the result — files are untouched and dynamics are not compressed.
Unified preamp: applies to EQ and convolution together — set manually or auto-compensated from the combined response peak to prevent clipping.
Channel & headphone effects: balance, L/R swap, mono mixdown, headphone crossfeed, stereo width.
DSP master switch: bypass all effects with one tap.
(6) Lyrics & Desktop Lyrics
Scrolling lyrics: lyrics scroll on the playing page by timestamp, with per-word animated effects and multiple easing modes.
Automatic lyrics matching: optional in settings (writing lyrics to files is done manually from the song properties page).
Desktop lyrics: a floating desktop lyrics window with karaoke mode, position lock, and global shortcuts; it can hide automatically while the now-playing detail page is in the foreground.
(7) Flexible Playback Modes
Sequential playback: play every track in playlist order, ideal for enjoying a full album or a curated playlist.
Shuffle: break the fixed order and let every click bring a surprise — great for discovering gems in your library.
Single loop: lock in a favorite track and let the melody repeat endlessly.
(8) Library Management
Classic categories: browse by song, album, artist, folder, and favorites, locating any track in one click.
Favorites: add songs to a dedicated favorites list.
Custom playlists: easily create, edit, and delete personal playlists to organize music your way.
(9) SMTC Integration
Original album art display and timeline via System Media Transport Controls.
(10) Highly Customizable Settings
App styles: Mica, Acrylic, transparent Acrylic, and custom Acrylic with adjustable opacity.
App themes: system default, dark, and light.
(11) Global Shortcuts
13 built-in global shortcuts, all re-recordable in settings: play/pause (Ctrl+Alt+P), next track (Ctrl+Alt+→), previous track (Ctrl+Alt+←), volume up/down (Ctrl+Alt+↑/↓), toggle now-playing details (Ctrl+Alt+Q), back (Ctrl+Alt+B), show/hide main window (Ctrl+Alt+W), toggle fullscreen (Ctrl+Alt+F), plus desktop lyrics show/hide (Ctrl+Alt+D), lock/unlock (Ctrl+Alt+L), karaoke mode (Ctrl+Alt+K), and reset window (Ctrl+Alt+R).
3. Installation & Usage
(1) System Requirements
Windows 10 19041 or later (Windows 11 recommended) for smooth, stable playback.
(2) First Use
After launching, add folders containing music files — the player scans them and builds your local library.
Navigate from the sidebar to switch between playlist pages and start exploring.
To add more music, click "Add Folder" and pick a folder to scan. In the playing view, choose the right output mode (WASAPI exclusive, WASAPI shared, DirectSound) and output device (built-in speakers, headphones, Bluetooth speakers, etc.) for your scenario and sound-quality needs.
4. GitHub
Main program (including the standalone AudioPlayer engine): https://github.com/Johnwikix/original-sound-hq-player
5. Feedback
QQ groups for feedback and discussion: 1009034363 / 1033738779"""

ES_DESC = """Reproductor HQ de Sonido Original - Sonido Auténtico, Escucha Sin Límites
1. Descripción del Producto
El Reproductor HQ de Sonido Original es una aplicación WinUI dedicada a brindar a los amantes de la música una experiencia de sonido original máxima. Se centra en la reproducción de audio de alta calidad y rompe los límites de la escucha convencional gracias a una excelente tecnología. La aplicación funciona con un motor de audio independiente basado en decodificación FFmpeg e interoperación WASAPI/ASIO desarrollada a medida, y procesa toda la cadena en coma flotante de 64 bits. Admite más de 12 formatos de música, incluidos DSD (DSF/DFF), FLAC, WAV, MP3, AAC, M4A, OGG/OGA, Opus, WMA, AIFF, APE y WavPack (WV), y puede convertir el audio compatible a WAV, FLAC, ALAC, MP3, AAC, OGG y Opus (formatos con pérdida con bitrate seleccionable). La salida de audio admite WASAPI exclusivo/compartido, DirectSound, ASIO y DSD DoP/Native.
Importante — prueba y compra: La aplicación ofrece una prueba gratuita con todas las funciones disponibles (la duración se muestra en la página de la tienda). Tras la prueba, la reproducción local sigue funcionando — la reproducción básica, el ecualizador de 10 bandas y el resto de efectos estándar siguen siendo gratis, mientras que la corrección por convolución, la salida DSD (DoP/Native), la salida 5.1 y el passthrough Atmos por HDMI requieren la compra de la versión completa. Durante la restricción, los ajustes guardados no se modifican ni se borran, y se restauran automáticamente tras la compra. El primer inicio solicita aceptar el acuerdo de usuario y la política de privacidad integrados.
2. Características Funcionales
(1) Información Musical Completa
Detalles en tiempo real: la interfaz de reproducción muestra el título, el artista, el álbum, la duración, la frecuencia de muestreo, el bitrate y el tipo de archivo en tiempo real.
Coincidencia de metadatos: las portadas se buscan automáticamente por el título de la canción.
(2) Compatibilidad con Sony Walkman
Transfiere fácilmente por USB la música con metadatos ya coincididos a tu Walkman, incluidos los archivos de letras ya asociados.
(3) Gestión Eficiente de Carpetas
Adición y escaneo manuales: selecciona carpetas locales para escanearlas e importar rápidamente tu colección a la biblioteca. El reescaneo automático y la vigilancia de carpetas mantienen la biblioteca al día: los registros existentes se actualizan por ruta y se eliminan los de archivos inexistentes, para que la biblioteca siempre esté ordenada y al día.
(4) Múltiples Modos de Salida
Modo exclusivo WASAPI: modos de push y de eventos; el reproductor toma el control exclusivo del dispositivo de audio, reduciendo interferencias y latencia para una salida de alta fidelidad.
Modo compartido WASAPI: el reproductor comparte el dispositivo de audio con otras aplicaciones.
Modo DirectSound: buena aceleración por hardware, especialmente eficaz en escenas de audio complejas o multicanal.
DSD DoP: salida DSD original encapsulada en tramas PCM.
DSD Native: salida DSD original a través de ASIO.
ASIO: salida ASIO nativa con enumeración automática de controladores y negociación de búfer/frecuencia, incluidas las extensiones DSD nativas de ASIO.
Multicanal e inmersivo: salida PCM 5.1 y passthrough Atmos (E-AC-3/JOC) por HDMI (requiere un receptor compatible; versión completa).
Fiabilidad: un perro guardián de salida se recupera automáticamente; el formato y la conexión/desconexión de dispositivos no interrumpen la reproducción; los dispositivos se seleccionan por ID de punto de conexión estables, así que un reconectar nunca elige la salida equivocada.
(5) Efectos DSP Profesionales
Ecualizador de 10 bandas: ganancia y Q por banda, con múltiples presets guardables.
Corrección por convolución: dibuja una curva (2–32 puntos arrastrables) o importa una respuesta de impulso (IR) WAV de auriculares/sala; se convierte en un FIR de fase mínima en tiempo real; puede vincularse por dispositivo de salida y aplicarse automáticamente al conectar o cambiar dispositivos; la edición es instantánea y cuenta con vista previa de la respuesta combinada EQ/convolución.
Normalización de sonoridad: análisis EBU R128 por pista en segundo plano, aplica una ganancia fija suave y guarda el resultado en caché, sin modificar archivos ni comprimir la dinámica.
Preamplificador unificado: se aplica a EQ y convolución; manual o compensado automáticamente según el pico de la respuesta combinada para evitar recortes.
Efectos de canal y auriculares: balance, intercambio L/R, mezcla mono, crossfeed y ancho estéreo.
Interruptor maestro DSP: desactiva todos los efectos con un toque.
(6) Letras y Letras de Escritorio
Letras en desplazamiento: se desplazan según la marca de tiempo en la página de reproducción, con animaciones por palabra y varios modos de suavizado.
Coincidencia automática de letras: opcional en la configuración (la escritura en archivos se hace manualmente desde la página de propiedades de la canción).
Letras de escritorio: ventana flotante de letras con modo karaoke, bloqueo de posición y atajos globales; puede ocultarse automáticamente con la página de reproducción en primer plano.
(7) Modos de Reproducción
Reproducción secuencial: reproduce cada pista en el orden de la lista, ideal para disfrutar un álbum completo o una lista cuidada.
Aleatorio: rompe el orden fijo y deja que cada clic traiga una sorpresa, perfecta para descubrir joyas de tu biblioteca.
Repetición de pista: fija tu canción favorita y deja que la melodía se repita sin fin.
(8) Gestión de Listas
Clasificación clásica: organiza por canciones, álbumes, artistas, carpetas y favoritos para localizar cualquier pista con un clic.
Favoritos: añade canciones a una lista de favoritos dedicada.
Listas personalizadas: crea, edita y elimina listas personales con facilidad para organizar tu música a tu manera.
(9) Integración SMTC
Portada original del álbum y línea de tiempo con los controles multimedia del sistema.
(10) Configuración Muy Personalizable
Estilos de aplicación: Mica, Acrílico, Acrílico transparente y Acrílico personalizado con opacidad ajustable.
Temas: sistema, oscuro y claro.
(11) Atajos Globales
13 atajos globales integrados, todos redefinibles en la configuración: reproducir/pausar (Ctrl+Alt+P), pista siguiente (Ctrl+Alt+→), pista anterior (Ctrl+Alt+←), subir/bajar volumen (Ctrl+Alt+↑/↓), detalles de reproducción (Ctrl+Alt+Q), volver (Ctrl+Alt+B), mostrar/ocultar la ventana principal (Ctrl+Alt+W), pantalla completa (Ctrl+Alt+F), además de mostrar/ocultar las letras de escritorio (Ctrl+Alt+D), bloquear/desbloquear (Ctrl+Alt+L), modo karaoke (Ctrl+Alt+K) y restablecer la ventana (Ctrl+Alt+R).
3. Instalación y Uso
(1) Requisitos del Sistema
Windows 10 19041 o posterior (se recomienda Windows 11) para una reproducción fluida y estable.
(2) Primer Uso
Tras iniciar la aplicación, añade manualmente carpetas con archivos de música; el reproductor las escaneará e importará a tu biblioteca local.
Navega desde la barra lateral para cambiar entre las páginas de listas y empezar a explorar tu música.
Para añadir más música, pulsa «Añadir carpeta» y elige la carpeta a escanear. En la interfaz de reproducción puedes elegir el modo de salida (WASAPI exclusivo, WASAPI compartido, DirectSound) y el dispositivo (altavoces integrados, auriculares, altavoz Bluetooth, etc.) según tu escenario y necesidades de sonido.
4. GitHub
Programa principal (incluido el motor de audio independiente AudioPlayer): https://github.com/Johnwikix/original-sound-hq-player
5. Comentarios
Grupos de QQ para comentarios y soporte: 1009034363 / 1033738779"""

# ------------------------------------------------------------ ShortDescription

ZH_SHORT = ("支持 DSD（DSF/DFF）、FLAC、WAV、MP3、AAC、M4A、OGG、Opus、WMA、AIFF、APE、WavPack 等多种音乐格式，"
            "可转换为 WAV/FLAC/ALAC/MP3/AAC/OGG/Opus；音频输出支持 WASAPI 独占/共享、DirectSound、ASIO 与 DSD DoP/Native。"
            "免费试用，完整版一次性买断。问题反馈交流QQ群：一群 1009034363，二群 1033738779")
EN_SHORT = ("Supports 12+ music formats including DSD (DSF/DFF), FLAC, WAV, MP3, AAC, M4A, OGG, Opus, WMA, AIFF, APE, and WavPack. "
            "Convert to WAV/FLAC/ALAC/MP3/AAC/OGG/Opus. Output via WASAPI exclusive/shared, DirectSound, ASIO, and DSD DoP/Native. "
            "Free trial with a one-time full-version purchase. Feedback QQ groups: 1009034363 / 1033738779")
ES_SHORT = ("Admite más de 12 formatos: DSD (DSF/DFF), FLAC, WAV, MP3, AAC, M4A, OGG, Opus, WMA, AIFF, APE y WavPack. "
            "Convierte a WAV/FLAC/ALAC/MP3/AAC/OGG/Opus. Salida por WASAPI exclusivo/compartido, DirectSound, ASIO y DSD DoP/Native. "
            "Prueba gratuita con compra única de la versión completa. Grupos de QQ: 1009034363 / 1033738779")

# ----------------------------------------------------------------------- main

with open(CSV_PATH, encoding="utf-8-sig", newline="") as f:
    rows = list(csv.reader(f))

for name, t, lim in (
    ("zh notes", ZH_NOTES, NOTES_LIMIT), ("en notes", EN_NOTES, NOTES_LIMIT), ("es notes", ES_NOTES, NOTES_LIMIT),
    ("zh desc", ZH_DESC, DESC_LIMIT), ("en desc", EN_DESC, DESC_LIMIT), ("es desc", ES_DESC, DESC_LIMIT),
    ("zh short", ZH_SHORT, SHORT_LIMIT), ("en short", EN_SHORT, SHORT_LIMIT), ("es short", ES_SHORT, SHORT_LIMIT),
):
    assert len(t) <= lim, f"{name} too long: {len(t)} > {lim}"

rows[1][4], rows[1][5], rows[1][6] = ZH_DESC, EN_DESC, ES_DESC
rows[2][4], rows[2][5], rows[2][6] = ZH_NOTES, EN_NOTES, ES_NOTES
rows[7][4], rows[7][5], rows[7][6] = ZH_SHORT, EN_SHORT, ES_SHORT

with open(CSV_PATH, "w", encoding="utf-8-sig", newline="") as f:
    w = csv.writer(f, lineterminator="\r\n")
    w.writerows(rows)

print("written. lengths:",
      {"notes": (len(ZH_NOTES), len(EN_NOTES), len(ES_NOTES)),
       "desc": (len(ZH_DESC), len(EN_DESC), len(ES_DESC)),
       "short": (len(ZH_SHORT), len(EN_SHORT), len(ES_SHORT))})
