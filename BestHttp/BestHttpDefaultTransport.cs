#if PFOUND_BESTHTTP
using UnityEngine;

namespace PFound.ContentDelivery.Transport
{
    /// <summary>
    /// Makes <see cref="BestHttpTransport"/> the default download transport whenever this adapter is compiled
    /// in (the <c>PFOUND_BESTHTTP</c> define). Runs before the first scene loads so any
    /// <see cref="ContentDeliveryBootstrap.InitializeAsync"/> call picks BestHTTP unless overridden. The
    /// foundation keeps no hard reference to BestHTTP — the optional assembly reaches in, not the reverse.
    /// </summary>
    internal static class BestHttpDefaultTransport
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => ContentDeliveryBootstrap.DefaultTransport = new BestHttpTransport();
    }
}
#endif
