namespace qbPortWeaver
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
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
