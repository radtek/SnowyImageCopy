﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;

using SnowyImageCopy.Common;
using SnowyImageCopy.Helper;
using SnowyImageCopy.Models.Exceptions;
using SnowyImageCopy.Models.Network;
using SnowyImageCopy.Models.Toast;
using SnowyImageCopy.Properties;
using SnowyImageCopy.ViewModels;

namespace SnowyImageCopy.Models
{
	/// <summary>
	/// Run operations.
	/// </summary>
	public class Operation : NotificationObject
	{
		/// <summary>
		/// Instance of MainWindowViewModel
		/// </summary>
		private readonly MainWindowViewModel mainWindowViewModelInstance;

		public Operation(MainWindowViewModel mainWindowViewModelInstance)
		{
			this.mainWindowViewModelInstance = mainWindowViewModelInstance;
		}


		#region Access to MainWindowViewModel property

		private string OperationStatus
		{
			set { mainWindowViewModelInstance.OperationStatus = value; }
		}

		private FileItemViewModelCollection FileListCore
		{
			get { return mainWindowViewModelInstance.FileListCore; }
		}

		private ListCollectionView FileListCoreView
		{
			get { return mainWindowViewModelInstance.FileListCoreView; }
		}

		private int FileListCoreViewIndex
		{
			set { mainWindowViewModelInstance.FileListCoreViewIndex = value; }
		}


		private FileItemViewModel CurrentItem
		{
			set { mainWindowViewModelInstance.CurrentItem = value; }
		}

		private byte[] CurrentImageData
		{
			get { return mainWindowViewModelInstance.CurrentImageData; }
			set { mainWindowViewModelInstance.CurrentImageData = value; }
		}


		private bool IsWindowActivateRequested
		{
			set { mainWindowViewModelInstance.IsWindowActivateRequested = value; }
		}

		#endregion


		#region Operation state

		/// <summary>
		/// Checking files in FlashAir card.
		/// </summary>
		public bool IsChecking
		{
			get { return _isChecking; }
			set
			{
				_isChecking = value;
				RaisePropertyChanged();
			}
		}
		private bool _isChecking;

		/// <summary>
		/// Copying files from FlashAir card.
		/// </summary>
		public bool IsCopying
		{
			get { return _isCopying; }
			set
			{
				_isCopying = value;
				RaisePropertyChanged();
			}
		}
		private bool _isCopying;

		/// <summary>
		/// Running timer for auto check.
		/// </summary>
		internal bool IsAutoRunning
		{
			get { return _isAutoRunning; }
			set
			{
				_isAutoRunning = value;
				RaisePropertyChanged();
			}
		}
		private bool _isAutoRunning;

		/// <summary>
		/// Progress of operation
		/// </summary>
		public ProgressInfo OperationProgress
		{
			get { return _operationProgress; }
			set
			{
				_operationProgress = value;
				RaisePropertyChanged();
			}
		}
		private ProgressInfo _operationProgress;

		#endregion


		#region Timer for auto check

		private DispatcherTimer autoTimer;

		public void StartAutoTimer()
		{
			IsAutoRunning = true;
			ResetAutoTimer();
		}

		private void StopAutoTimer()
		{
			IsAutoRunning = false;
			ResetAutoTimer();
		}

		public void ResetAutoTimer()
		{
			if (IsAutoRunning)
			{
				if (autoTimer == null)
				{
					autoTimer = new DispatcherTimer();
					autoTimer.Tick += OnAutoTimerTick;
				}

				autoTimer.Stop();
				autoTimer.Interval = TimeSpan.FromSeconds(Settings.Current.AutoCheckInterval);
				autoTimer.Start();
				OperationStatus = Resources.OperationStatus_WaitingAutoCheck;
			}
			else
			{
				if (autoTimer == null)
					return;

				autoTimer.Stop();
				autoTimer = null;
				SystemSounds.Exclamation.Play();
				OperationStatus = Resources.OperationStatus_Stopped;
			}
		}

		private async void OnAutoTimerTick(object sender, EventArgs e)
		{
			if (IsChecking || IsCopying)
				return;

			autoTimer.Stop();

			var isConnected = await NetworkChecker.IsNetworkConnectedAsync(card);
			var isUpdated = false;

			if (isConnected)
			{
				try
				{
					isUpdated = await CheckUpdateStatusAsync();
				}
				catch (RemoteConnectionUnableException)
				{
					isConnected = false;
				}
				catch (Exception ex)
				{
					OperationStatus = Resources.OperationStatus_Error;
					Debug.WriteLine("Failed to check update status. {0}", ex);
					throw;
				}
			}

			if (isConnected)
			{
				if (isUpdated || (LastCheckTime.Add(checkThresholdTime) < DateTime.Now))
				{
					await CheckCopyFileAsync();
				}
			}
			else
			{
				OperationStatus = Resources.OperationStatus_ConnectionUnable;
			}

			if (IsAutoRunning)
			{
				autoTimer.Start();
				OperationStatus = Resources.OperationStatus_WaitingAutoCheck;
			}
		}

		#endregion


		private readonly CardInfo card = new CardInfo();

		private CancellationTokenSource tokenSourceWorking;
		private CancellationTokenSource tokenSourceLoading;

		private DateTime LastCheckTime { get; set; }
		private readonly TimeSpan checkThresholdTime = TimeSpan.FromMinutes(10);

		internal DateTime CopyStartTime { get; private set; }
		private int fileCopiedSum;

		private readonly TimeSpan toastThresholdTime = TimeSpan.FromSeconds(30);


		#region Method (Public or Internal)

		/// <summary>
		/// Check & Copy files in FlashAir card.
		/// </summary>
		/// <remarks>This method is called by Command.</remarks>
		public async Task CheckCopyFileAsync()
		{
			if (!IsReady())
				return;				

			try
			{
				IsChecking = true;
				IsCopying = true;

				await CheckFileBaseAsync();

				OperationProgress = null;

				SelectFileBase();

				OperationProgress = null;

				await CopyFileBaseAsync(new Progress<ProgressInfo>(x => OperationProgress = x));

				IsChecking = false;
				IsCopying = false;

				await ShowToast();
			}
			catch (OperationCanceledException)
			{
				SystemSounds.Exclamation.Play();
				OperationStatus = Resources.OperationStatus_Stopped;
			}
			catch (Exception ex)
			{
				SystemSounds.Hand.Play();

				if (ex.GetType() == typeof(RemoteConnectionUnableException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionUnable;
				}
				else if (ex.GetType() == typeof(RemoteConnectionLostException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionLost;
				}
				else if (ex.GetType() == typeof(TimeoutException))
				{
					OperationStatus = Resources.OperationStatus_TimedOut;
				}
				else
				{
					OperationStatus = Resources.OperationStatus_Error;
					Debug.WriteLine("Failed to check & copy files. {0}", ex);
					throw;
				}
			}
			finally
			{
				// In case any exception happened.
				IsChecking = false;
				IsCopying = false;
			}
		}

		/// <summary>
		/// Check files in FlashAir card.
		/// </summary>
		/// <remarks>This method is called by Command.</remarks>
		public async Task CheckFileAsync()
		{
			if (!IsReady())
				return;

			try
			{
				IsChecking = true;

				await CheckFileBaseAsync();

				OperationProgress = null;
			}
			catch (OperationCanceledException)
			{
				SystemSounds.Exclamation.Play();
				OperationStatus = Resources.OperationStatus_Stopped;
			}
			catch (Exception ex)
			{
				SystemSounds.Hand.Play();

				if (ex.GetType() == typeof(RemoteConnectionUnableException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionUnable;
				}
				else if (ex.GetType() == typeof(RemoteConnectionLostException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionLost;
				}
				else if (ex.GetType() == typeof(TimeoutException))
				{
					OperationStatus = Resources.OperationStatus_TimedOut;
				}
				else
				{
					OperationStatus = Resources.OperationStatus_Error;
					Debug.WriteLine("Failed to check files. {0}", ex);
					throw;
				}
			}
			finally
			{
				IsChecking = false;
			}
		}

		/// <summary>
		/// Copy files from FlashAir card.
		/// </summary>
		/// <remarks>This method is called by Command.</remarks>
		public async Task CopyFileAsync()
		{
			if (!IsReady())
				return;

			try
			{
				IsCopying = true;

				OperationProgress = null;

				await CopyFileBaseAsync(new Progress<ProgressInfo>(x => OperationProgress = x));

				IsCopying = false;

				await ShowToast();
			}
			catch (OperationCanceledException)
			{
				SystemSounds.Exclamation.Play();
				OperationStatus = Resources.OperationStatus_Stopped;
			}
			catch (Exception ex)
			{
				SystemSounds.Hand.Play();

				if (ex.GetType() == typeof(RemoteConnectionUnableException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionUnable;
				}
				else if (ex.GetType() == typeof(RemoteConnectionLostException))
				{
					OperationStatus = Resources.OperationStatus_ConnectionLost;
				}
				else if (ex.GetType() == typeof(TimeoutException))
				{
					OperationStatus = Resources.OperationStatus_TimedOut;
				}
				else
				{
					OperationStatus = Resources.OperationStatus_Error;
					Debug.WriteLine("Failed to copy files. {0}", ex);
					throw;
				}
			}
			finally
			{
				// In case any exception happened.
				IsCopying = false;
			}
		}

		/// <summary>
		/// Stop operation.
		/// </summary>
		/// <remarks>This method is called by Command.</remarks>
		public void Stop()
		{
			StopAutoTimer();

			if ((tokenSourceWorking == null) || (tokenSourceWorking.IsCancellationRequested))
				return;

			try
			{
				tokenSourceWorking.Cancel();
			}
			catch (ObjectDisposedException ode)
			{
				Debug.WriteLine("CancellationTokenSource has been disposed when tried to cancel operation. {0}", ode);
			}
		}

		/// <summary>
		/// Check update status of FlashAir card.
		/// </summary>
		internal async Task<bool> CheckUpdateStatusAsync()
		{
			OperationStatus = Resources.OperationStatus_Checking;

			var isUpdated = await FileManager.CheckUpdateStatusAsync();

			OperationStatus = Resources.OperationStatus_Completed;

			return isUpdated;
		}

		/// <summary>
		/// Load image data from local file of a specified item and set it to current image data.
		/// </summary>
		/// <param name="item">Target item</param>
		internal async Task LoadSetFileAsync(FileItemViewModel item)
		{
			if ((tokenSourceLoading != null) && (!tokenSourceLoading.IsCancellationRequested))
			{
				try
				{
					tokenSourceLoading.Cancel();
				}
				catch (ObjectDisposedException ode)
				{
					Debug.WriteLine("CancellationTokenSource has been disposed when tried to cancel operation. {0}", ode);
					throw;
				}
			}

			try
			{
				await LoadSetFileBaseAsync(item);
			}
			catch (OperationCanceledException)
			{
				// None.
			}
			catch (Exception ex)
			{
				Debug.WriteLine("Failed to load image data from local file. {0}", ex);
				throw;
			}
		}

		#endregion


		#region Method (Private)

		/// <summary>
		/// Check if ready for operation.
		/// </summary>
		/// <returns>True if ready</returns>
		private bool IsReady()
		{
			if (!NetworkChecker.IsNetworkConnected())
			{
				OperationStatus = Resources.OperationStatus_NoNetwork;
				return false;
			}

			if ((Settings.Current.TargetPeriod == FilePeriod.Select) &&
				!Settings.Current.TargetDates.Any())
			{
				OperationStatus = Resources.OperationStatus_NoDateCalender;
				return false;
			}

			return true;
		}

		/// <summary>
		/// Check files in FlashAir card.
		/// </summary>
		private async Task CheckFileBaseAsync()
		{
			OperationStatus = Resources.OperationStatus_Checking;

			try
			{
				tokenSourceWorking = new CancellationTokenSource();

				// Check SSID.
				var ssid = await FileManager.GetSsidAsync(tokenSourceWorking.Token);
				if (!String.IsNullOrEmpty(ssid) && (card.Ssid != ssid))
				{
					if (FileListCore.Any())
						FileListCore.Clear();

					card.Ssid = ssid;

					// Check if PC is connected to FlashAir card by wireless network.
					var checkTask = Task.Run(async () =>
						card.IsWirelessConnected = await NetworkChecker.IsWirelessNetworkConnectedAsync(ssid));
				}

				// Get all items.
				var fileListNew = await FileManager.GetFileListRootAsync(tokenSourceWorking.Token, card);
				fileListNew.Sort();

				// Remove sample items.
				var itemsSample = FileListCore.Where(x => x.Size == 0).ToList();
				if (itemsSample.Any())
				{
					itemsSample.ForEach(x => FileListCore.Remove(x));
				}

				// Check old items.
				foreach (var itemOld in FileListCore)
				{
					var itemBuff = fileListNew.FirstOrDefault(x => x.FilePath == itemOld.FilePath);
					if ((itemBuff != null) && (itemBuff.Size == itemOld.Size))
					{
						fileListNew.Remove(itemBuff);

						itemOld.IsRemoteAlive = true;
						itemOld.IsLocalAlive = IsCopiedLocal(itemOld);
						itemOld.Status = itemOld.IsLocalAlive ? FileStatus.Copied : FileStatus.NotCopied;
						continue;
					}

					itemOld.IsRemoteAlive = false;
				}

				// Add new items.
				var isLeadOff = true;
				foreach (var itemNew in fileListNew)
				{
					if (isLeadOff)
					{
						FileListCoreViewIndex = FileListCoreView.IndexOf(itemNew);
						isLeadOff = false;
					}

					itemNew.IsRemoteAlive = true;
					itemNew.IsLocalAlive = IsCopiedLocal(itemNew);
					itemNew.Status = itemNew.IsLocalAlive ? FileStatus.Copied : FileStatus.NotCopied;

					FileListCore.Insert(itemNew); // Customized Insert method
				}

				// Manage deleted items.
				var itemsDeleted = FileListCore.Where(x => !x.IsRemoteAlive && (x.Status != FileStatus.Recycled)).ToArray();
				if (itemsDeleted.Any())
				{
					if (Settings.Current.WillMoveFileToRecycle)
					{
						var itemsDeletedCopied = itemsDeleted.Where(x => x.Status == FileStatus.Copied).ToList();

						Recycle.MoveToRecycle(itemsDeletedCopied.Select(ComposeLocalPath));

						itemsDeletedCopied.ForEach(x => x.Status = FileStatus.Recycled);
					}

					for (int i = itemsDeleted.Length - 1; i >= 0; i--)
					{
						if ((itemsDeleted[i].Status == FileStatus.Copied) ||
							(itemsDeleted[i].Status == FileStatus.Recycled))
							continue;

						FileListCore.Remove(itemsDeleted[i]);
					}
				}
				
				// Get thumbnails (from local).
				foreach (var item in FileListCore)
				{
					if (!item.IsTarget || item.HasThumbnail || (item.Status != FileStatus.Copied) || !item.IsLocalAlive || !item.CanLoadLocal)
						continue;

					tokenSourceWorking.Token.ThrowIfCancellationRequested();

					try
					{
						if (item.CanReadExif)
						{
							item.Thumbnail = await ImageManager.ReadThumbnailAsync(ComposeLocalPath(item));
						}
						else if (item.CanLoadLocal)
						{
							item.Thumbnail = await ImageManager.CreateThumbnailAsync(ComposeLocalPath(item));
						}
					}
					catch (FileNotFoundException)
					{
						item.Status = FileStatus.NotCopied;
						item.IsLocalAlive = false;
					}
				}

				// Get thumbnails (from remote).
				foreach (var item in FileListCore)
				{
					if (!item.IsTarget || item.HasThumbnail || (item.Status == FileStatus.Copied) || !item.IsRemoteAlive || !item.CanGetThumbnailRemote)
						continue;

					tokenSourceWorking.Token.ThrowIfCancellationRequested();

					try
					{
						item.Thumbnail = await FileManager.GetThumbnailAsync(item.FilePath, tokenSourceWorking.Token, card);
					}
					catch (RemoteFileNotFoundException)
					{
						item.IsRemoteAlive = false;
					}
				}

				OperationStatus = Resources.OperationStatus_CheckCompleted;
				LastCheckTime = DateTime.Now;
			}
			finally
			{
				FileListCoreViewIndex = -1; // No selection

				if (tokenSourceWorking != null)
				{
					try
					{
						tokenSourceWorking.Dispose();
					}
					finally
					{
						tokenSourceWorking = null;
					}
				}
			}
		}

		/// <summary>
		/// Select files to be copied from FlashAir card.
		/// </summary>
		private void SelectFileBase()
		{
			foreach (var item in FileListCore)
			{
				if (item.IsTarget && item.IsRemoteAlive && (item.Status == FileStatus.NotCopied))
					item.Status = FileStatus.ToBeCopied;
			}
		}

		/// <summary>
		/// Copy files from FlashAir card.
		/// </summary>
		/// <param name="progress">Progress</param>
		private async Task CopyFileBaseAsync(IProgress<ProgressInfo> progress)
		{
			CopyStartTime = DateTime.Now;
			fileCopiedSum = 0;

			if (FileListCore.All(x => x.Status != FileStatus.ToBeCopied))
			{
				OperationStatus = Resources.OperationStatus_NoFileToBeCopied;
				return;
			}

			OperationStatus = Resources.OperationStatus_Copying;

			try
			{
				tokenSourceWorking = new CancellationTokenSource();

				// Check SSID.
				var ssid = await FileManager.GetSsidAsync(tokenSourceWorking.Token);
				if (!String.IsNullOrEmpty(ssid) && (card.Ssid != ssid))
				{
					OperationStatus = Resources.OperationStatus_NotSameFlashAir;
					return;
				}

				while (true)
				{
					tokenSourceWorking.Token.ThrowIfCancellationRequested();

					var item = FileListCore.FirstOrDefault(x => x.Status == FileStatus.ToBeCopied);
					if (item == null)
						break; // Completed copying.

					try
					{
						item.Status = FileStatus.Copying;

						FileListCoreViewIndex = FileListCoreView.IndexOf(item);

						var localPath = ComposeLocalPath(item);

						var localDirectory = Path.GetDirectoryName(localPath);
						if (!String.IsNullOrEmpty(localDirectory) && !Directory.Exists(localDirectory))
							Directory.CreateDirectory(localDirectory);

						var data = await FileManager.GetSaveFileAsync(item.FilePath, localPath, item.Size, item.Date, item.CanReadExif, progress, tokenSourceWorking.Token, card);

						CurrentItem = item;
						CurrentImageData = data;

						if (!item.HasThumbnail)
						{
							if (item.CanReadExif)
								item.Thumbnail = await ImageManager.ReadThumbnailAsync(CurrentImageData);
							else if (item.CanLoadLocal)
								item.Thumbnail = await ImageManager.CreateThumbnailAsync(CurrentImageData);
						}

						item.FileCopiedTime = DateTime.Now;
						item.IsLocalAlive = true;
						item.Status = FileStatus.Copied;

						fileCopiedSum++;
					}
					catch (RemoteFileNotFoundException)
					{
						item.IsRemoteAlive = false;
					}
					catch (RemoteFileInvalidException)
					{
						item.Status = FileStatus.Weird;
					}
					catch
					{
						item.Status = FileStatus.ToBeCopied; // Revert to status before copying.
						throw;
					}
				}

				OperationStatus = String.Format(Resources.OperationStatus_CopyCompleted, fileCopiedSum, (int)(DateTime.Now - CopyStartTime).TotalSeconds);
			}
			finally
			{
				FileListCoreViewIndex = -1; // No selection

				if (tokenSourceWorking != null)
				{
					try
					{
						tokenSourceWorking.Dispose();
					}
					finally
					{
						tokenSourceWorking = null;
					}
				}
			}
		}

		/// <summary>
		/// Show a toast to notify copy completed.
		/// </summary>
		private async Task ShowToast()
		{
			if (!OsVersion.IsEightOrNewer)
				return;

			if ((fileCopiedSum <= 0) || (DateTime.Now - CopyStartTime < toastThresholdTime))
				return;

			var t = new ToastManager();
			var result = await t.ShowAsync(
				Resources.ToastHeadline_CopyCompleted,
				Resources.ToastBody_CopyCompleted1st,
				String.Format(Resources.ToastBody_CopyCompleted2nd, fileCopiedSum, (int)(DateTime.Now - CopyStartTime).TotalSeconds));

			if (result == ToastResult.Activated)
				IsWindowActivateRequested = true; // Activating Window is requested.
		}

		/// <summary>
		/// Load image data from local file of a specified item and set it to current image data.
		/// </summary>
		/// <param name="item">Target item</param>
		private async Task LoadSetFileBaseAsync(FileItemViewModel item)
		{
			var localPath = ComposeLocalPath(item);

			try
			{
				tokenSourceLoading = new CancellationTokenSource();

				byte[] data = null;
				if (item.CanLoadLocal)
					data = await FileAddition.ReadAllBytesAsync(localPath, tokenSourceLoading.Token);

				CurrentItem = item;
				CurrentImageData = data;
			}
			finally
			{
				if (tokenSourceLoading != null)
				{
					try
					{
						tokenSourceLoading.Dispose();
					}
					finally
					{
						tokenSourceLoading = null;
					}
				}
			}
		}

		#endregion


		#region Helper

		/// <summary>
		/// Compose path to local file of a specified item.
		/// </summary>
		/// <param name="item">Target item</param>
		private string ComposeLocalPath(FileItemViewModel item)
		{
			if (String.IsNullOrEmpty((item.FileName)))
				throw new InvalidOperationException("FileName property is empty.");

			var fileName = item.FileName;

			if (Settings.Current.WillMakeFileExtensionLowerCase)
			{
				var extension = Path.GetExtension(fileName);
				if (!String.IsNullOrEmpty(extension))
					fileName = Path.GetFileNameWithoutExtension(fileName) + extension.ToLower();
			}

			return Path.Combine(Settings.Current.LocalFolder, item.Date.ToString("yyyyMMdd"), fileName);
		}

		/// <summary>
		/// Check if local file of a specified item exists.
		/// </summary>
		/// <param name="item">Target item</param>
		private bool IsCopiedLocal(FileItemViewModel item)
		{
			var localPath = ComposeLocalPath(item);

			return (File.Exists(localPath) && (new FileInfo(localPath).Length == item.Size));
		}

		#endregion
	}
}
