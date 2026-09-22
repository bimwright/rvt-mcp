// Plugin shells disable generated assembly metadata. Restore their Windows-only
// contract for the platform analyzer without annotating the cross-platform server.
#if NET8_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
#endif
