using ReactiveUI;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.BlockFilters;
using WalletWasabi.Blockchain.Blocks;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

public partial class WalletLoadWorkflow : IWalletLoadWorkflow
{
	private readonly CompositeDisposable _disposables = new();
	private readonly Wallet _wallet;
	private Stopwatch? _stopwatch;
	private uint _filtersToDownloadCount;
	private uint _filtersToProcessCount;
	private uint _filterProcessStartingHeight;
	private Subject<(double PercentComplete, TimeSpan TimeRemaining)> _progress;
	[AutoNotify] private bool _isLoading;

	public WalletLoadWorkflow(Wallet wallet)
	{
		_wallet = wallet;
		_progress = new();
		_progress.OnNext((0, TimeSpan.Zero));

		LoadCompleted =
			Observable.FromEventPattern<WalletState>(_wallet, nameof(Wallet.StateChanged))
					  .ObserveOn(RxApp.MainThreadScheduler)
					  .Select(x => x.EventArgs)
					  .Where(x => x == WalletState.Started || (x == WalletState.Starting && wallet.KeyManager.SkipSynchronization))
					  .ToSignal();
	}

	public IObservable<(double PercentComplete, TimeSpan TimeRemaining)> Progress => _progress;

	public IObservable<Unit> LoadCompleted { get; }

	private uint TotalCount => _filtersToProcessCount + _filtersToDownloadCount;

	private uint RemainingFiltersToDownload => (uint)Services.BitcoinStore.SmartHeaderChain.HashesLeft;

	public void Start()
	{
		_stopwatch = Stopwatch.StartNew();
		_disposables.Add(Disposable.Create(_stopwatch.Stop));

		Services.Synchronizer.WhenAnyValue(x => x.BackendStatus)
							 .Where(status => status == BackendStatus.Connected)
							 .SubscribeAsync(async _ => await LoadWalletAsync(isBackendAvailable: true).ConfigureAwait(false))
							 .DisposeWith(_disposables);

		Observable.FromEventPattern<bool>(Services.Synchronizer, nameof(Services.Synchronizer.ResponseArrivedIsGenSocksServFail))
				  .SubscribeAsync(async _ =>
				  {
					  if (Services.Synchronizer.BackendStatus == BackendStatus.Connected)
					  {
						  return;
					  }

					  await LoadWalletAsync(isBackendAvailable: false).ConfigureAwait(false);
				  })
				 .DisposeWith(_disposables);

		Observable.Interval(TimeSpan.FromSeconds(1))
				  .ObserveOn(RxApp.MainThreadScheduler)
				  .Subscribe(_ =>
				  {
					  var processedCount = GetCurrentProcessedCount();
					  UpdateProgress(processedCount);
				  })
				  .DisposeWith(_disposables);
	}

	public void Stop()
	{
		_disposables.Dispose();
	}

	private async Task LoadWalletAsync(bool isBackendAvailable)
	{
		if (IsLoading)
		{
			return;
		}

		IsLoading = true;

		await SetInitValuesAsync(isBackendAvailable).ConfigureAwait(false);

		while (isBackendAvailable && RemainingFiltersToDownload > 0 && !_wallet.KeyManager.SkipSynchronization)
		{
			await Task.Delay(1000).ConfigureAwait(false);
		}

		if (_wallet.State != WalletState.Uninitialized)
		{
			throw new Exception("Wallet is already being logged in.");
		}

		try
		{
			await Task.Run(async () => await Services.WalletManager.StartWalletAsync(_wallet));
		}
		catch (OperationCanceledException ex)
		{
			Logger.LogTrace(ex);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	private async Task SetInitValuesAsync(bool isBackendAvailable)
	{
		while (isBackendAvailable && Services.Synchronizer.LastResponse is null)
		{
			await Task.Delay(500).ConfigureAwait(false);
		}

		_filtersToDownloadCount = (uint)Services.BitcoinStore.SmartHeaderChain.HashesLeft;

		if (Services.BitcoinStore.SmartHeaderChain.ServerTipHeight is { } serverTipHeight &&
			Services.BitcoinStore.SmartHeaderChain.TipHeight is { } clientTipHeight)
		{
			var tipHeight = Math.Max(serverTipHeight, clientTipHeight);
			var startingHeight = SmartHeader.GetStartingHeader(_wallet.Network, IndexType.SegwitTaproot).Height;
			var bestHeight = (uint)_wallet.KeyManager.GetBestHeight().Value;
			_filterProcessStartingHeight = bestHeight < startingHeight ? startingHeight : bestHeight;

			_filtersToProcessCount = tipHeight - _filterProcessStartingHeight;
		}
	}

	private uint GetCurrentProcessedCount()
	{
		uint downloadedFilters = 0;
		if (_filtersToDownloadCount > 0)
		{
			downloadedFilters = _filtersToDownloadCount - RemainingFiltersToDownload;
		}

		uint processedFilters = 0;
		if (_wallet.LastProcessedFilter?.Header?.Height is { } lastProcessedFilterHeight)
		{
			processedFilters = lastProcessedFilterHeight - _filterProcessStartingHeight - 1;
		}

		var processedCount = downloadedFilters + processedFilters;

		return processedCount;
	}

	private void UpdateProgress(uint processedCount)
	{
		if (TotalCount == 0 || processedCount == 0 || processedCount > TotalCount || _stopwatch is null)
		{
			return;
		}

		var percent = (decimal)processedCount / TotalCount * 100;
		var remainingCount = TotalCount - processedCount;
		var tempPercent = (uint)Math.Round(percent);

		if (tempPercent == 0)
		{
			return;
		}

		var remainingMilliseconds = (double)_stopwatch.ElapsedMilliseconds / processedCount * remainingCount;
		var remainingTimeSpan = TimeSpan.FromMilliseconds(remainingMilliseconds);

		if (remainingTimeSpan > TimeSpan.FromHours(1))
		{
			remainingTimeSpan = new TimeSpan(remainingTimeSpan.Days, remainingTimeSpan.Hours, remainingTimeSpan.Minutes, seconds: 0);
		}

		_progress.OnNext((tempPercent, remainingTimeSpan));
	}
}
