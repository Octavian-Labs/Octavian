// OctaIgnite — мгновенный запуск двигателя вертолётов.
// Перекраска InstantStartup (Kaysharp): миникоптер + скрап-верт
// (ScrapTransportHelicopter наследует Minicopter — одна проверка).
namespace Oxide.Plugins
{
    [Info("OctaIgnite", "Octavian", "1.0.0")]
    [Description("Instant engine startup for Minicopter and Scrap Transport Helicopter")]
    public class OctaIgnite : RustPlugin
    {
        private const string PermUse = "octaignite.use";

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
        }

        private void OnEngineStarted(BaseMountable entity, BasePlayer player)
        {
            if (player == null || !permission.UserHasPermission(player.UserIDString, PermUse))
                return;

            if (player.GetMountedVehicle() is Minicopter heli)
                heli.engineController.FinishStartingEngine();
        }
    }
}
