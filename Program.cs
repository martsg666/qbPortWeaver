namespace qbPortWeaver
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == AppConstants.VpnAutoRecoveryArg)
            {
                // args[1] is the service name extracted from the triggering event by the task's
                // ValueQueries binding (Event/EventData/Data).
                // args[2] is the user's LocalAppData path baked in at task-creation time.
                // Overriding before LogManager.Initialize() ensures all paths resolve correctly
                // when this process runs as SYSTEM via the scheduled task.
                string serviceName = args.Length >= 2 ? args[1] : string.Empty;
                if (args.Length >= 3 && Directory.Exists(args[2]))
                    AppConstants.OverrideLocalAppData(args[2]);
                LogManager.Initialize(AppConstants.GetLogFilePath());
                VpnAutoRecoveryManager.ExecuteRecovery(serviceName);
                return;
            }

            Application.SetColorMode(SystemColorMode.System);
            ApplicationConfiguration.Initialize();

            // Enforce single instance using a named mutex.
            // Using initiallyOwned: false + WaitOne(0) instead of the initiallyOwned: true constructor
            // overload so that an AbandonedMutexException (thrown when a previous instance crashed
            // without releasing the mutex) can be caught and treated as "we are the new instance".
            // The OS transfers ownership to us when the mutex is abandoned, so the catch is safe.
            using var mutex = new Mutex(false, "Global\\qbPortWeaver_SingleInstance");
            bool isNewInstance;
            try   { isNewInstance = mutex.WaitOne(0, false); }
            catch (AbandonedMutexException) { isNewInstance = true; }

            if (!isNewInstance)
            {
                MessageBox.Show(
                    $"{AppConstants.AppName} is already running.",
                    AppConstants.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            Application.Run(new MainForm());
        }
    }
}
