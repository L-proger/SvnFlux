using System.CommandLine;
using System.Runtime.InteropServices;
using SvnFlux.Subversion.Interop;

namespace SvnFlux.Playground.Scenarios;

internal static class SubversionNativeScenario {
    public static Command CreateCommand() {
        var command = new Command("subversion-native", "Load the packaged native Subversion libraries and call their P/Invoke API.");
        command.SetAction(_ => Run());
        return command;
    }

    public static Command CreateCheckoutCommand() {
        var rootOption = new Option<string>("--root") {
            Description = "Parent directory for generated repositories and working copies.",
            DefaultValueFactory = _ => Path.Combine(".playground-data", "native-subversion")
        };
        var command = new Command("subversion-checkout", "Create an official SVN repository and check it out through the packaged P/Invoke API.");
        command.Options.Add(rootOption);
        command.SetAction(parseResult => CreateRepositoryAndCheckout(parseResult.GetValue(rootOption)!));
        return command;
    }

    private static unsafe int Run() {
        var version = libsvn_client_1.svn_client_version();
        if (version is null) {
            Console.Error.WriteLine("svn_client_version returned null.");
            return 1;
        }

        var tag = Marshal.PtrToStringUTF8((nint)version->tag);
        Console.WriteLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Subversion: {version->major}.{version->minor}.{version->patch}{tag}");
        Console.WriteLine("SvnFlux.Subversion P/Invoke package loaded successfully.");
        return 0;
    }

    private static unsafe int CreateRepositoryAndCheckout(string root) {
        var runRoot = Path.Combine(Path.GetFullPath(root), $"run-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        var repositoryPath = Path.Combine(runRoot, "repository");
        var destination = Path.Combine(runRoot, "working-copy");
        Directory.CreateDirectory(runRoot);
        apr_pool_t* pool = null;
        var aprInitialized = false;
        nint nativeRepositoryPath = 0;
        nint nativeUrl = 0;
        nint nativeDestination = 0;
        nint nativeUsernameParameter = 0;
        nint nativeUsername = 0;

        try {
            var status = libapr_1.apr_initialize();
            if (status != 0) {
                throw new InvalidOperationException($"apr_initialize failed with status {status}.");
            }
            aprInitialized = true;

            status = libapr_1.apr_pool_create_ex(&pool, null, null, null);
            if (status != 0 || pool is null) {
                throw new InvalidOperationException($"apr_pool_create_ex failed with status {status}.");
            }

            ThrowIfError(libsvn_subr_1.svn_dso_initialize2());
            ThrowIfError(libsvn_fs_1.svn_fs_initialize(pool));
            ThrowIfError(libsvn_ra_1.svn_ra_initialize(pool));

            nativeRepositoryPath = Marshal.StringToCoTaskMemUTF8(repositoryPath);
            var canonicalRepositoryPath = libsvn_subr_1.svn_dirent_canonicalize((sbyte*)nativeRepositoryPath, pool);
            if (canonicalRepositoryPath is null) throw new InvalidOperationException("svn_dirent_canonicalize returned null.");

            svn_repos_t* repository;
            ThrowIfError(libsvn_repos_1.svn_repos_create(&repository, canonicalRepositoryPath, null, null, null, null, pool));

            apr_hash_t* config;
            ThrowIfError(libsvn_subr_1.svn_config_get_config(&config, null, pool));

            svn_client_ctx_t* context;
            ThrowIfError(libsvn_client_1.svn_client_create_context2(&context, config, pool));

            svn_auth_provider_object_t* usernameProvider;
            libsvn_subr_1.svn_auth_get_username_provider(&usernameProvider, pool);
            var providers = libapr_1.apr_array_make(pool, 1, sizeof(nint));
            if (providers is null) throw new InvalidOperationException("apr_array_make returned null.");
            *(svn_auth_provider_object_t**)libapr_1.apr_array_push(providers) = usernameProvider;

            svn_auth_baton_t* authentication;
            libsvn_subr_1.svn_auth_open(&authentication, providers, pool);
            nativeUsernameParameter = Marshal.StringToCoTaskMemUTF8("svn:auth:username");
            nativeUsername = Marshal.StringToCoTaskMemUTF8(Environment.UserName);
            libsvn_subr_1.svn_auth_set_parameter(authentication, (sbyte*)nativeUsernameParameter, (void*)nativeUsername);
            context->auth_baton = authentication;

            var repositoryUrl = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
            nativeUrl = Marshal.StringToCoTaskMemUTF8(repositoryUrl);
            nativeDestination = Marshal.StringToCoTaskMemUTF8(destination);
            var canonicalUrl = libsvn_subr_1.svn_uri_canonicalize((sbyte*)nativeUrl, pool);
            if (canonicalUrl is null) throw new InvalidOperationException("svn_uri_canonicalize returned null.");
            var revision = new svn_opt_revision_t { kind = svn_opt_revision_kind.svn_opt_revision_head };
            var checkedOutRevision = 0;

            Console.WriteLine($"Repository: {repositoryPath}");
            Console.WriteLine($"Repository URL: {repositoryUrl}");
            Console.WriteLine($"Working copy: {destination}");
            ThrowIfError(libsvn_client_1.svn_client_checkout4(
                &checkedOutRevision,
                canonicalUrl,
                (sbyte*)nativeDestination,
                &revision,
                &revision,
                svn_depth_t.svn_depth_infinity,
                ignore_externals: 0,
                allow_unver_obstructions: 0,
                wc_format_version: null,
                svn_tristate_t.svn_tristate_unknown,
                context,
                pool));

            Console.WriteLine($"Checked out revision {checkedOutRevision} through libsvn_client.");
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally {
            if (nativeDestination != 0) Marshal.FreeCoTaskMem(nativeDestination);
            if (nativeUrl != 0) Marshal.FreeCoTaskMem(nativeUrl);
            if (nativeRepositoryPath != 0) Marshal.FreeCoTaskMem(nativeRepositoryPath);
            if (nativeUsername != 0) Marshal.FreeCoTaskMem(nativeUsername);
            if (nativeUsernameParameter != 0) Marshal.FreeCoTaskMem(nativeUsernameParameter);
            if (pool is not null) libapr_1.apr_pool_destroy(pool);
            if (aprInitialized) libapr_1.apr_terminate();
        }
    }

    private static unsafe void ThrowIfError(svn_error_t* error) {
        if (error is null) return;

        var parts = new List<string>();
        var code = error->apr_err;
        for (var current = error; current is not null; current = current->child) {
            var message = Marshal.PtrToStringUTF8((nint)current->message);
            if (!string.IsNullOrWhiteSpace(message)) parts.Add(message);
        }

        libsvn_subr_1.svn_error_clear(error);
        throw new InvalidOperationException($"Subversion error E{code}: {string.Join(" -> ", parts)}");
    }
}
