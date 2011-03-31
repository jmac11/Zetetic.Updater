﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Xml.Serialization;
using System.Threading;
using System.Net;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using CAPICOM;
using System.Windows;
using System.Windows.Input;
using NLog;

namespace Zetetic.Updater
{
    public class UpdateWindowViewModel : INotifyPropertyChanged
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public virtual event PropertyChangedEventHandler PropertyChanged;

        public virtual void OnPropertyChanged(object target, string propertyName)
        {
            if (PropertyChanged != null) PropertyChanged(target ?? this, new PropertyChangedEventArgs(propertyName));
        }

        private Thread _backgroundThread = null;

        public UpdateWindowViewModel(Application app, string updateUrl)
        {
            App = app;
            UpdateUri = updateUrl;
            DoCheck = true;
            _backgroundThread = new Thread(new ThreadStart(this.CheckForUpdates));
            _backgroundThread.Start();
        }

        #region Properties

        private Application _app;
        public virtual Application App
        {
            get { return _app; }
            set
            {
                _app = value;
                OnPropertyChanged(this, "App");
            }
        }

        private string _updateUri;
        public virtual string UpdateUri
        {
            get { return _updateUri; }
            set
            {
                _updateUri = value;
                OnPropertyChanged(this, "UpdateUri");
            }
        }

        private ReleaseManifest _manifest;
        public virtual ReleaseManifest Manifest
        {
            get { return _manifest; }
            set
            {
                _manifest = value;
                OnPropertyChanged(this, "Manifest");
                OnPropertyChanged(this, "UpdateLabel");
            }
        }

        private bool _doUpdate;
        public virtual bool DoUpdate
        {
            get { return _doUpdate; }
            set
            {
                _doUpdate = value;
                OnPropertyChanged(this, "DoUpdate");
            }
        }

        private bool _doCheck;
        public virtual bool DoCheck
        {
            get { return _doCheck; }
            set
            {
                _doCheck = value;
                OnPropertyChanged(this, "DoCheck");
            }
        }

        public virtual string UpdateLabel
        {
            get 
            {
                return Manifest == null ? null : string.Format("A new version of {0}, {1}, is now available. Would you like to download it?", Manifest.Name, Manifest.Version);
            }
        }

        #endregion

        #region Commands

        public EventHandler UpdateBegin;
        public EventHandler UpdateProgress;
        public EventHandler UpdateComplete;
        public EventHandler UpdateError;

        private ICommand _updateCommand;
        public ICommand UpdateCommand
        {
            get
            {
                if (_updateCommand == null) _updateCommand = new RelayCommand(
                    (o) =>
                    {
                        DoCheck = false; // no further checks after the user provides input

                        if (UpdateBegin != null) UpdateBegin(o, EventArgs.Empty);
                        
                        try
                        {
                            WebRequest fileRequest = WebRequest.Create(Manifest.PackageUrl);
                            WebResponse fileResponse = fileRequest.GetResponse();

                            string installerPath = Path.GetTempPath() + @"\" + GetFilenameFromUrl(Manifest.PackageUrl);
                            using (Stream stream = fileResponse.GetResponseStream())
                            {
                                ReadStreamToFile(stream, installerPath, (int)fileResponse.ContentLength);
                            }

                            CheckSignature(installerPath); // will throw on error
                            Process.Start(installerPath);
                            Thread.Sleep(2000);
                            if (UpdateComplete != null) UpdateComplete(o, EventArgs.Empty);
                            App.Shutdown();
                        }
                        catch (Exception e)
                        {
                            logger.WarnException("an error occured processing update", e);
                            if (UpdateError != null) UpdateError(e, EventArgs.Empty);
                        }
                    },
                    (o) =>
                    {
                        return Manifest != null;
                    }
                );
                return _updateCommand;
            }
        }

        private ICommand _cancelCommand;
        public ICommand CancelCommand
        {
            get
            {
                if (_cancelCommand == null) _cancelCommand = new RelayCommand(
                    (o) =>
                    {
                        DoCheck = false;
                        ((Window)o).Close();
                    },
                    (o) =>
                    {
                        return o != null;
                    }
                );
                return _cancelCommand;
            }
        }
        #endregion

        public void StopAsync()
        {
            _backgroundThread.Abort();
        }

        public EventHandler UpdateAvailable;

        public delegate void ProgressUpdateHandler(int percent);
        public event ProgressUpdateHandler ProgressUpdate;

        public void CheckForUpdates()
        {
            XmlSerializer serializer = new XmlSerializer(typeof(ReleaseManifest));
            while (DoCheck)
            {
                try
                {
                    WebRequest request = WebRequest.Create(UpdateUri);
                    WebResponse response = request.GetResponse();
                    if (response.ContentLength > 0)
                    {
                        using (TextReader reader = new StreamReader(response.GetResponseStream()))
                        {
                            Manifest = (ReleaseManifest)serializer.Deserialize(reader);

                            Version manifestVersion = new Version(Manifest.Version);
                            Version runningVersion = Assembly.GetEntryAssembly().GetName().Version;

                            if (manifestVersion.CompareTo(runningVersion) > 0)
                            {
                                logger.Info("Manifest version {0} is greater than current application version {1}", manifestVersion, runningVersion);

                                if (UpdateAvailable != null) UpdateAvailable(this, EventArgs.Empty);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.ErrorException(string.Format("unable to fetch path {0}", UpdateUri), ex);
                }
                if(DoCheck) System.Threading.Thread.Sleep(60 * 60 * 1000);
            }
        }

        private string GetFilenameFromUrl(string url)
        {
            Uri uri = new Uri(url);
            string[] segments = uri.Segments;
            return Uri.UnescapeDataString(segments[segments.Length - 1]);
        }

        private void ReadStreamToFile(Stream stream, string path, int fileSize)
        {
            int length = fileSize;
            int bufferSize = 1024 * 10;
            byte[] buffer = new byte[bufferSize];

            if (fileSize > 0)
            {
                logger.Info("preparing to read {0} bytes of data from download into file {1}", length, path);
                using (FileStream fileStream = File.Open(path, FileMode.Create, FileAccess.Write))
                {
                    while (length > 0)
                    {
                        int toRead = (bufferSize > length) ? length : bufferSize;
                        int bytesRead = stream.Read(buffer, 0, toRead);
                        if (bytesRead == 0)
                        {
                            throw new Exception("server disconnected");
                        }
                        length -= bytesRead;
                        fileStream.Write(buffer, 0, bytesRead);
                        int pctComplete = (int)((((double)(fileSize - length)) / ((double)fileSize)) * 100.0);
                        if (ProgressUpdate != null) ProgressUpdate(pctComplete);
                    }
                }
            }
        }

        private void CheckSignature(string path)
        {
            string currentPath = Assembly.GetEntryAssembly().Location;

            X509Certificate msiCert = null;
            X509Certificate appCert = null;

            // step 1 - extract the certificates from the signed files
            try
            {
                logger.Debug("Extracting X509 certificate from MSI at {0}", path);
                msiCert = X509Certificate.CreateFromSignedFile(path);
            }
            catch (CryptographicException e)
            {
                logger.Warn("MSI at {0} is not properly signed {1}", path, e.Message);
            }

            try
            {
                logger.Debug("Extracting X509 certificate from currently running application at {0}", currentPath);
                appCert = X509Certificate.CreateFromSignedFile(currentPath);
            }
            catch (Exception e)
            {
                logger.Warn("application at {0} is not signed {1}", currentPath, e.Message);
            }

            // step 2, check the certificates to make sure they match
            if (appCert == null && msiCert == null)
            {
                // if neither file is signed, skip the check
                logger.Warn("no signatures present on installer or running application, skipping validity check");
                return;
            }
            else if (appCert != null && msiCert != null)
            {
                // if both files are signed, compare the subjects and make sure they are the same
                if (!string.IsNullOrEmpty(msiCert.Subject) &&
                    string.Compare(msiCert.Subject, appCert.Subject, StringComparison.InvariantCultureIgnoreCase) == 0)
                {
                    logger.Info("verified subject of MSI against current application certificate");
                }
                else
                {
                    throw new Exception(
                        string.Format("Installer certificate subject {0} does not match current application certificate subject {1}.", msiCert.Subject, appCert.Subject)
                    );
                }
            }

            // step 3. verify the signature on the installer is good
            logger.Debug("initializing CAPICOM SignedCode object");
            CAPICOM.SignedCode signedFile = new CAPICOM.SignedCode();

            logger.Debug("Setting verification file path to {0}", path);
            signedFile.FileName = path;

            logger.Debug("Verifying file");
            signedFile.Verify(false); // throws an exception if the signature is invalid
        }
    }
}