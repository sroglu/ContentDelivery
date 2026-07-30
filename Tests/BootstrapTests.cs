using NUnit.Framework;
using PFound.ContentDelivery.Core;
using PFound.ContentDelivery.Transport;

namespace PFound.ContentDelivery.Tests
{
    /// <summary>
    /// The default-transport seam (G6): a transport is always available, and it is overridable — which is
    /// exactly the hook the BestHTTP adapter uses to install itself as the default when the
    /// <c>PFOUND_BESTHTTP</c> define is present. The concrete default type is intentionally NOT asserted
    /// (it is UnityWebRequest unless BestHTTP self-registered), only that the seam holds.
    /// </summary>
    public sealed class BootstrapTests
    {
        [Test]
        public void DefaultTransport_IsAlwaysPresent_AndOverridable()
        {
            var original = ContentDeliveryBootstrap.DefaultTransport;
            try
            {
                Assert.IsNotNull(ContentDeliveryBootstrap.DefaultTransport, "a default transport must always be available");

                IDownloadTransport replacement = new UnityWebRequestTransport();
                ContentDeliveryBootstrap.DefaultTransport = replacement;
                Assert.AreSame(replacement, ContentDeliveryBootstrap.DefaultTransport, "the default must be overridable (BestHTTP uses this)");
            }
            finally
            {
                ContentDeliveryBootstrap.DefaultTransport = original;
            }
        }
    }
}
