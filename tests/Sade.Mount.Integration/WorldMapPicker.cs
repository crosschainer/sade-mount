// Exercise Sandbox's actual launch setup, map-source button and selection callback.
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var package = await Package.Fetch("facepunch.sandbox", false);
Check(package?.Info.UsesCreateGameModal == true, "Use the normal Sandbox game package.");
var sys = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ModalSystem", false)).FirstOrDefault(t => t is not null)
    ?? throw new Exception("The menu modal system is unavailable.");
var instance = sys.GetField("Instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!.GetValue(null);
object[] Modals() => ((System.Collections.IEnumerable)sys.GetField("OpenModals", flags)!.GetValue(instance)!).Cast<object>().ToArray();
foreach (var existing in Modals().OfType<Sandbox.UI.Panel>().Where(p => p.GetType().Name == "MapSelectorModal").ToArray())
    existing.GetType().GetMethod("CloseModal", flags)!.Invoke(existing, [false]);
Game.Overlay.CreateGame(new Sandbox.Modals.CreateGameOptions
{
    Package = package, OnComplete = _ => { },
    InitialSettings = new Sandbox.Modals.CreateGameResults { Map = "facepunch.flatgrass" }
});
Sandbox.UI.Panel? page = null; Sandbox.UI.Panel? selector = null; Sandbox.UI.Panel? pickerPanel = null;
for (var i = 0; i < 100 && selector is null; i++)
{
    await Task.Delay(200);
    page = Modals().OfType<Sandbox.UI.Panel>().FirstOrDefault(p => p.GetType().Name == "GameModal");
    selector = page?.Descendants.FirstOrDefault(p => p.GetType().FullName == "SboxMenu.MapSelector");
}
Check(selector is not null, "Sandbox's New game map row must be available.");
try
{
    selector!.GetType().GetMethod("OpenSelector")!.Invoke(selector, null);
    await Task.Delay(250);
    var picker = Modals().OfType<Sandbox.UI.Panel>().Single(p => p.GetType().Name == "MapSelectorModal");
    pickerPanel = picker;
    Check(picker.Descendants.OfType<Sandbox.UI.Label>().Any(l => l.Text?.Contains("GTA San Andreas") == true), "GTA must appear in the picker source list before selecting it.");
    var owner = picker.GetType().BaseType!;
    owner.GetMethod("SelectSource", flags)!.Invoke(picker, ["sade"]);
    var maps = ((System.Collections.IEnumerable)owner.GetField("mountedMaps", flags)!.GetValue(picker)!).Cast<Sandbox.Mounting.ResourceLoader>().ToArray();
    Check(maps.Length == 1 && maps[0].Name == "San Andreas", "The full world must appear once, rather than as separate cities.");
    var thumbnail = (maps[0] as Sandbox.Mounting.IThumbnailProvider)?.Thumbnail;
    Check(thumbnail is { Width: >= 640, Height: >= 360 } && !thumbnail.IsError, "The map tile must have its actual preview image.");
    owner.GetMethod("SelectMountedMap", flags)!.Invoke(picker, [maps[0].Path]);
    var setup = page!.GetType().GetProperty("Setup", flags)!.GetValue(page)!;
    var result = (Sandbox.Modals.CreateGameResults)setup.GetType().GetMethod("Finish")!.Invoke(setup, null)!;
    Check(result.Map == maps[0].Path, "The launch result must preserve the mounted scene path.");
    foreach (var path in new[] { "san_andreas", "San Andreas - Grove Street", "san_andreas_san_fierro", "San Andreas - San Fierro", "san_andreas_las_venturas", "San Andreas - Las Venturas" })
        Check(Sandbox.Mounting.Directory.Get("sade").GetByPath("mount://sade/maps/" + path + ".scene") is not null, "Saved map reference must still resolve: " + path);
    return new { Passed = true, Name = maps[0].Name, result.Map, Preview = new { thumbnail.Width, thumbnail.Height }, OldPathsPreserved = true };
}
finally
{
    if (pickerPanel is { IsValid: true }) pickerPanel.GetType().GetMethod("CloseModal", flags)!.Invoke(pickerPanel, [false]);
    if (page is not null) page.GetType().GetMethod("CloseModal", flags)!.Invoke(page, [false]);
}
