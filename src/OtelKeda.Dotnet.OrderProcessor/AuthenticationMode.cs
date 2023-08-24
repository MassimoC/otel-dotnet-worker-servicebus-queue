namespace OtelKeda.Dotnet.OrderProcessor
{
    public enum AuthenticationMode
    {
        ConnectionString,
        ServicePrinciple,
        PodIdentity,
        WorkloadIdentity
    }
}
