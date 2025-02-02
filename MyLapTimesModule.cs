using AssettoServer.Server.Plugin;
using Autofac;

namespace MyLapTimesPlugin;

public class MyLapTimesModule : AssettoServerModule<MyLapTimesConfiguration>
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<MyLapTimesPlugin>()
               .AsSelf()
               .AutoActivate()    // Create it immediately
               .SingleInstance(); // Exactly one instance
    }
}