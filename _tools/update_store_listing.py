# -*- coding: utf-8 -*-
"""Update the Microsoft Store listing CSV for v1.2.3.0 (paid transition)."""
import csv, shutil, sys

CSV_PATH = r"H:\download\listingData-9NFW1RPPT999-1152921505701887236.csv"
LIMIT = 1500  # Partner Center ReleaseNotes character limit

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

ZH_TRIAL = ("【重要｜版本与购买】本应用提供免费试用，试用期内可使用全部功能（试用时长以商店页面为准）。"
            "试用结束后仍可正常播放本地音乐，基础播放、十段均衡器及其余常规音效保持免费；"
            "卷积校正、DSD 位流输出（DoP/Native）、5.1 多声道输出与 Atmos HDMI 直通需购买完整版解锁。"
            "受限期间已保存的设置不会被修改或清除，购买后自动恢复生效。首次启动需阅读并同意用户协议与隐私政策。")
EN_TRIAL = ("Important — trial & purchase: The app offers a free trial with every feature available "
            "(trial length as shown on the Store page). After the trial, local playback keeps working — "
            "core playback, the 10-band equalizer and other standard effects stay free, while convolution "
            "correction, DSD bitstream output (DoP/Native), 5.1 multichannel output and Atmos HDMI passthrough "
            "require the full-version purchase. While restricted, saved settings are never modified or erased "
            "and are restored automatically after purchase. The first launch asks you to accept the built-in "
            "user agreement and privacy policy.")
ES_TRIAL = ("Importante — prueba y compra: La aplicación ofrece una prueba gratuita con todas las funciones "
            "disponibles (la duración se muestra en la página de la tienda). Tras la prueba, la reproducción "
            "local sigue funcionando — la reproducción básica, el ecualizador de 10 bandas y el resto de efectos "
            "estándar siguen siendo gratis, mientras que la corrección por convolución, la salida DSD (DoP/Native), "
            "la salida 5.1 y el passthrough Atmos por HDMI requieren la compra de la versión completa. Durante la "
            "restricción, los ajustes guardados no se modifican ni se borran, y se restauran automáticamente tras "
            "la compra. El primer inicio solicita aceptar el acuerdo de usuario y la política de privacidad integrados.")

def insert_before(text, anchor, block):
    assert text.count(anchor) == 1, f"anchor not unique: {anchor!r} x{text.count(anchor)}"
    return text.replace(anchor, block + "\n" + anchor, 1)

with open(CSV_PATH, encoding="utf-8-sig", newline="") as f:
    rows = list(csv.reader(f))

for name, notes in (("zh-cn", ZH_NOTES), ("en", EN_NOTES), ("es", ES_NOTES)):
    assert len(notes) <= LIMIT, f"{name} release notes too long: {len(notes)} > {LIMIT}"

# ReleaseNotes (row 2)
rows[2][4], rows[2][5], rows[2][6] = ZH_NOTES, EN_NOTES, ES_NOTES

# Description (row 1): insert trial/purchase block before the features heading
rows[1][4] = insert_before(rows[1][4], "二、功能特性", ZH_TRIAL)
rows[1][5] = insert_before(rows[1][5], "2. Functional Features", EN_TRIAL)
rows[1][6] = insert_before(rows[1][6], "2. Características Funcionales", ES_TRIAL)

# ShortDescription (row 7)
assert "directsound输出。问题反馈交流QQ群" in rows[7][4]
rows[7][4] = rows[7][4].replace("directsound输出。", "directsound输出。免费试用，完整版一次性买断。", 1)
rows[7][5] = rows[7][5].rstrip() + " Free trial with a one-time full-version purchase."
rows[7][6] = rows[7][6].rstrip() + " Prueba gratuita con compra única de la versión completa."

with open(CSV_PATH, "w", encoding="utf-8-sig", newline="") as f:
    w = csv.writer(f, lineterminator="\r\n")
    w.writerows(rows)

print("written. ReleaseNotes lengths:",
      {n: len(t) for n, t in (("zh", ZH_NOTES), ("en", EN_NOTES), ("es", ES_NOTES))})
print("Description lengths:", [len(rows[1][i]) for i in (4, 5, 6)])
print("ShortDescription lengths:", [len(rows[7][i]) for i in (4, 5, 6)])
