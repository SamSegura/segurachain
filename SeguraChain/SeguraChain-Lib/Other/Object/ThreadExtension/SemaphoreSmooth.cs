using SeguraChain_Lib.Utility;
using System.Threading;
using System.Threading.Tasks;

namespace SeguraChain_Lib.Other.Object.ThreadExtension
{
    public class SemaphoreSmooth
    {

        private SemaphoreSlim _semaphore;

        public SemaphoreSmooth(int initialCount, int maxCount)
        {
            _semaphore = new SemaphoreSlim(initialCount, maxCount);
        }

        public async Task WaitAsync(CancellationToken token)
        {

            bool isLocked = false;

            while (!isLocked)
            {
                isLocked = await _semaphore.WaitAsync(100, token);
                if (!isLocked)
                    await Task.Delay(1, token);
            }
        }

        public async Task<bool> WaitAsync(int time, CancellationToken token)
        {
            bool isLocked = false;

            long timeStart = ClassUtility.GetCurrentTimestampInMillisecond();
            long timeEnd = timeStart + time;

            while (!isLocked)
            {
                isLocked = await _semaphore.WaitAsync(100, token);
                if (!isLocked)
                    await Task.Delay(1, token);
                timeStart += 100;
                if (timeStart >= timeEnd)
                {
                    break;
                }
            }

            return isLocked;
        }

        public async Task WaitAsync()
        {
            bool isLocked = false;

            while (!isLocked)
            {
                isLocked = await _semaphore.WaitAsync(100);
                if (!isLocked)
                    await Task.Delay(1);
            }
        }

        public void Wait(CancellationToken token)
        {
            _semaphore.Wait(token);
        }

        public void Wait()
        {
            _semaphore.Wait();
        }

        public void Release()
        {
            _semaphore.Release();
        }
    }
}
