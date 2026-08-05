// SMSLIBRE — headless screenshot of the running Avalonia app.
//
// Renders the real MainWindow with the Skia backend in a headless platform and
// saves a PNG, so the UI can be verified without a physical display.

using System;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SmsLibre.App;

string outPng = args.Length > 0 ? args[0] : "app_shot.png";

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var window = new MainWindow { Width = 1280, Height = 820 };
window.Show();

// Pump the dispatcher so layout + the initial map render complete.
for (int i = 0; i < 40; i++)
    Dispatcher.UIThread.RunJobs();

var frame = window.CaptureRenderedFrame();
if (frame is null) { Console.Error.WriteLine("no frame captured"); return 1; }
frame.Save(outPng);
Console.WriteLine($"Wrote {System.IO.Path.GetFullPath(outPng)}  ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
return 0;
