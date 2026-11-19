# FlowVideoStudio (.EXE) — Google Flow Otomasyon, Seslendirme & CapCut Kurgu Stüdyosu

Bu uygulama, verdiğiniz **Google Flow** proje sayfasını, **kendi seslendirme (TTS) sitenizi** ve **CapCut tarzı çok kanallı video kurgu + FFmpeg render motorunu** tek bir yerel Windows `.exe` (`FlowVideoStudio.exe`) içinde birleştirir.

## 📁 Dosya ve Klasör Yapısı

- [FlowVideoStudio.exe](file:///C:/Users/xd/.gemini/antigravity/scratch/FlowVideoStudio/FlowVideoStudio.exe) — Doğrudan çalıştırılabilir masaüstü uygulaması (Şu an açık ve çalışıyor).
- [Program.cs](file:///C:/Users/xd/.gemini/antigravity/scratch/FlowVideoStudio/Program.cs) — C# (.NET + WebView2 + FFmpeg) ana kaynak kodu.
- [config.json](file:///C:/Users/xd/.gemini/antigravity/scratch/FlowVideoStudio/config.json) — Google Flow URL, kendi seslendirme sitenizin URL'si ve CSS seçici ayarları.
- [ui/editor.html](file:///C:/Users/xd/.gemini/antigravity/scratch/FlowVideoStudio/ui/editor.html) — CapCut tarzı çok kanallı Timeline, önizleme ve FFmpeg Render arayüzü.
- [ui/tts_hub.html](file:///C:/Users/xd/.gemini/antigravity/scratch/FlowVideoStudio/ui/tts_hub.html) — Seslendirme sitesi bağlantı merkezi ve yerleşik ses üretim köprüsü.
- [Projects/DefaultProject/script.md](file:///C:/Users/xd/.gemini/antigravity/scratch/FlowVideoStudio/Projects/DefaultProject/script.md) — Sahne promptlarını ve seslendirme metinlerini tutan senaryo dosyası.
- `Projects/DefaultProject/videos/` — Google Flow'dan otomatik yakalanan sahne videoları (`sahne_01.mp4`, `sahne_02.mp4`...).
- `Projects/DefaultProject/audio/` — Seslendirme sitenizden otomatik yakalanan ses dosyaları (`seslendirme_01.mp3`/`.wav`).
- `Projects/DefaultProject/renders/` — FFmpeg ile birleştirilmiş nihai `.mp4` video çıktıları.

---

## 🚀 Adım Adım Kullanım

### 1. Sekme: Google Flow (`🎬 1. Google Flow`)
- Uygulama doğrudan verdiğiniz `https://flow.google.com/project/d98f8df7-c977-46ba-8dd8-f39f24714571/tool/1af05941-50b5-4c91-b5d1-d4df10f10273` adresini açar.
- **Kalıcı Google Oturumu:** İlk açılışta Google hesabınıza giriş yaptığınızda çerezler `UserData/` klasörüne kaydedilir; sonraki açılışlarda tekrar giriş istemez.
- **Otomatik Video Yakalama:**
  - Flow'da üretilen her videonun sağ üst köşesine otomatik olarak **`🎬 Studio'ya Aktar`** butonu eklenir.
  - Ayrıca üst bardaki **`⚡ Tam Otomasyon: AÇIK`** aktifken, `script.md` içindeki sahne sayısı kadar video üretildiğinde videoları otomatik olarak `Projects/DefaultProject/videos/` klasörüne indirir ve **2. Sekmeye (Seslendirme)** geçer.

### 2. Sekme: Kendi Seslendirme Siteniz (`🎙️ 2. Seslendirme Sitesi`)
- İlk açılışta gelen ekranda (veya üstteki adres çubuğunda) **kendi seslendirme sitenizin URL adresini** yazıp **`🌐 Git & Kaydet`** butonuna basın.
- Otomasyon tetiklendiğinde (veya üst bardaki **`🔊 script.md Seslendir`** butonuna bastığınızda):
  1. Sitenizde `window.startVoiceover(text, scenes)` fonksiyonu tanımlıysa doğrudan onu çağırır.
  2. Tanımlı değilse sitenizdeki metin kutusunu (`textarea`) otomatik bulup `script.md` içindeki seslendirme metnini doldurur ve **Seslendir / Üret** butonuna tıklar.
  3. Sitenizde oluşan `<audio>` sesini veya indirilen `.mp3`/`.wav` dosyasını otomatik yakalayıp `Projects/DefaultProject/audio/` klasörüne kaydeder ve **3. Sekmedeki CapCut Timeline'ına** dizer.

### 3. Sekme: CapCut Kurgu & FFmpeg Render (`✂️ 3. CapCut Kurgu & Render Stüdyosu`)
- **Sol Panel:** Yakalanan Flow videolarını, ses dosyalarını ve `script.md` sahne düzenleyicisini içerir. Her sahnenin yanındaki **`🎬 Promptu Flow'a Gönder`** butonuyla `script.md` içindeki promptu tek tıkla Flow'a gönderebilirsiniz.
- **Orta Panel:** Gerçek zamanlı çok kanallı (Video + Seslendirme + Altyazı) önizleme oynatıcısı. **`🔗 Video Süresini Sese Eşitle`** butonu ile videoların süresini/hızını seslendirme sürelerine otomatik hizalayabilirsiniz.
- **Alt Panel (Timeline):**
  - **Altyazı Kanalı**, **Flow Video Kanalı**, **Seslendirme Kanalı** ve **Arka Plan Müzik (BGM) Kanalı**.
  - Klipleri sürükleyip sıralayabilir, sağ/sol kenarlarından tutup kırpabilir (**Trim**), kırmızı imleçten ikiye bölebilir (**`✂️ Klibi Böl`**) veya hız/ses seviyesini sağdaki **Inspector** panelinden değiştirebilirsiniz.
- **Render (`🚀 FFmpeg Render Al (.MP4)`):**
  - `1920x1080 (16:9)`, `1080x1920 (9:16 Shorts/Reels)` veya `1080x1080 (1:1)` seçip **FFmpeg Render Al** butonuna bastığınızda tüm kurgu ve altyazılar birleştirilerek `Projects/DefaultProject/renders/` klasörüne yüksek kaliteli `.mp4` olarak çıkarılır.
