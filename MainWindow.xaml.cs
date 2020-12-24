using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using static PInvoke.Kernel32;

namespace DirStats
{
    /*public static class DirectoryExtension
    {
        // There are notable problems with this.
        // TODO: Check to see if a whole folder is skipped if 
        // a single file is inaccessible.
        // TODO: Determine differences between this and the
        // EnumerationOptions method.
        public static IEnumerable<T> Walk<T>(this IEnumerable<T> source)
        {
            var enumerator = source.GetEnumerator();
            bool? current = null;

            while (current ?? true) {
                try { current = enumerator.MoveNext(); }
                catch { current = null; }

                if (current ?? false)
                    yield return enumerator.Current;
            }
        }
    }*/

    public static class Extension
    {
        private const long OneKB = 1024;
        private const long OneMB = OneKB * 1024;
        private const long OneGB = OneMB * 1024;
        private const long OneTB = OneGB * 1024;

        public static string FormatSize(this int value, int decimalPlaces = 0)
        {
            return ((long)value).FormatSize(decimalPlaces);
        }

        public static string FormatSize(this long value, int decimalPlaces = 0)
        {
            var asTB = Math.Round((double)value / OneTB, decimalPlaces);
            var asGB = Math.Round((double)value / OneGB, decimalPlaces);
            var asMB = Math.Round((double)value / OneMB, decimalPlaces);
            var asKB = Math.Round((double)value / OneKB, decimalPlaces);
            // TODO: Make shorter.
            string chosen = asTB > 1 ? string.Format("{0} TB", asTB)
                : asGB > 1 ? string.Format("{0} GB", asGB)
                : asMB > 1 ? string.Format("{0} MB", asMB)
                : asKB > 1 ? string.Format("{0} KB", asKB)
                : string.Format("{0} B", Math.Round((double)value, decimalPlaces));
            return chosen;
        }
    }

    public class DisplayAttribute : Attribute
    {
        public bool IsDisplayed;

        public DisplayAttribute(bool display)
        {
            this.IsDisplayed = display;
        }
    }

    public class DataGridDisplayBehavior
    {

    }

    public class Volume : INotifyPropertyChanged
    {
        private bool analyze;
        private string name;
        private string label;
        private DriveType type;
        private string format;
        private string size;
        private long nsize;
        private string free;
        private long nfree;
        //private string root;

        public bool Analyze { get => analyze; set { analyze = value; OnPropertyChanged(); }}
        public string Name { get => name; set { name = value; OnPropertyChanged(); }}
        public string Label { get => label; set { label = value; OnPropertyChanged(); }}
        public DriveType Type { get => type; set { type = value; OnPropertyChanged(); }}
        public string Format { get => format; set { format = value; OnPropertyChanged(); }}
        public string Size { get => size; set { size = value; OnPropertyChanged(); }}

        [Display(false)]
        public long NSize { get => nsize; set { nsize = value; OnPropertyChanged(); }}
        public string Free { get => free; set { free = value; OnPropertyChanged(); }}
        [Display(false)]
        public long NFree { get => nfree; set { nfree = value; OnPropertyChanged(); }}
        //public string Root { get => root; set { root = value; OnPropertyChanged(); }}

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class EnumerationWorker : Progress<long>
    {   

        public void Run()
        {

        }
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public ObservableCollection<Volume> Volumes;
        public string SaveFolder;
        public BackgroundWorker Enumerator;

        public MainWindow()
        {
            InitializeComponent();
            AllocConsole();

            Volumes = new ObservableCollection<Volume>();
            SaveFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),
                    "DirStats");
            Directory.CreateDirectory(SaveFolder);

            Enumerator = new BackgroundWorker();
            Enumerator.WorkerReportsProgress = true;
            Enumerator.WorkerSupportsCancellation = true;
            Enumerator.DoWork += Enumerator_DoWork;
            Enumerator.RunWorkerCompleted += Enumerator_RunWorkerCompleted;
            Enumerator.ProgressChanged += Enumerator_ProgressChanged;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            VolumeGrid.ItemsSource = Volumes;
            foreach (var di in DriveInfo.GetDrives())
            {
                try {
                    Volumes.Add(new Volume {
                        Analyze = true,
                        Name = di.Name,
                        Label = di.VolumeLabel,
                        Type = di.DriveType,
                        Format = di.DriveFormat,
                        Size = di.TotalSize.FormatSize(2),
                        NSize = di.TotalSize,
                        Free = di.TotalFreeSpace.FormatSize(2),
                        NFree = di.TotalFreeSpace,
                    });
                } catch {
                    // Most likely FS is bad, could be others.
                    Volumes.Add(new Volume {
                        Analyze = false,
                        Name = di.Name,
                        Type = di.DriveType,
                    });
                }
            }

            // TODO: This should show mountpoints, we may need this
            // info to skip loops.
            //var mos = new ManagementObjectSearcher("SELECT * FROM Win32_Volume");
            //foreach (var v in mos.Get()) { }
        }

        public void Start_Click(object sender, RoutedEventArgs e)
        {
            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            Enumerator.RunWorkerAsync();
        }

        public void Stop_Click(object sender, RoutedEventArgs e)
        {
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            Enumerator.CancelAsync();
        }

        public void Show_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo {
                Arguments = SaveFolder,
                FileName = "explorer.exe",
            });
        }

        public void EnumerateAsync(string path, IProgress<long> progress)
        {
            // Magic happens with all this async, may need to make reduce
            // queueing overhead and message passing.
            foreach (var e in new DirectoryInfo(path).EnumerateFiles("*",
                new System.IO.EnumerationOptions {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                }))
            {
                //Console.WriteLine(e.Length + " " + e.FullName);
                progress?.Report(e.Length);
            }
            
            // We don't get here.
            Console.WriteLine("Done.");
        }

        public async void Enumerator_DoWork(object sender, DoWorkEventArgs e)
        {
            var vs = Volumes.Where(v => v.Analyze);
            long total = 0, n = 0;
            foreach (var v in vs)
                total += (v.NSize - v.NFree);

            // Calculate total size of all volumes, store, progress update
            // returns number that is added to total.
            var progress = new Progress<long>(bytes => {
                n += bytes;
                double p = n / total;
                Console.WriteLine(total + " " + n + " " + (total - n));
                //Enumerator.ReportProgress((int)p);
            });

            EnumerateAsync(@"C:\", progress);

            var cancel = new CancellationTokenSource();
        }

        public void Enumerator_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            Stop_Click(sender, new RoutedEventArgs());
        }

        public void Enumerator_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            ProgressBar.Value = e.ProgressPercentage;
        }
    }
}
