using System;
using System.Collections.Generic;
using System.Linq;

namespace MALSpider.Graph
{
    public class TimeCompressor
    {
        private readonly DateTime _minDate;
        private readonly DateTime _maxDate;
        private readonly List<DateTime> _sortedDates;
        private readonly Dictionary<DateTime, double> _compressedOffsets = new();
        private readonly double _totalCompressedDuration;
        public double CompressionFactor { get; }

        public TimeCompressor(DateTime minDate, DateTime maxDate, IEnumerable<DateTime> nodeDates)
        {
            _minDate = minDate;
            _maxDate = maxDate;

            var dates = nodeDates.ToList();
            dates.Add(minDate);
            dates.Add(maxDate);

            _sortedDates = dates.Where(d => d >= minDate && d <= maxDate)
                                .Distinct()
                                .OrderBy(d => d)
                                .ToList();

            double currentOffset = 0;
            _compressedOffsets[_sortedDates[0]] = 0;

            double threshold = MALSpiderConstants.TimeCompressionThresholdDays;

            for (int i = 0; i < _sortedDates.Count - 1; i++)
            {
                double deltaDays = (_sortedDates[i + 1] - _sortedDates[i]).TotalDays;
                double effectiveDelta = Math.Min(deltaDays, threshold);
                currentOffset += effectiveDelta;
                _compressedOffsets[_sortedDates[i + 1]] = currentOffset;
            }

            _totalCompressedDuration = currentOffset;
            if (_totalCompressedDuration <= 0) _totalCompressedDuration = 1;

            double totalUncompressedDays = (maxDate - minDate).TotalDays;
            if (totalUncompressedDays <= 0) totalUncompressedDays = 1;
            CompressionFactor = totalUncompressedDays / _totalCompressedDuration;
        }

        public double GetY(DateTime? date, double startY, double pixelsPerYear)
        {
            double scale = pixelsPerYear / 365.25;
            if (!date.HasValue) return startY + (_totalCompressedDuration * scale) + MALSpiderConstants.NodeHeight;

            DateTime d = date.Value;
            if (d <= _minDate) return startY;
            if (d >= _maxDate) return startY + (_totalCompressedDuration * scale);

            // Find the interval [d1, d2] that contains d
            int index = _sortedDates.BinarySearch(d);
            double compressedOffset;
            if (index >= 0)
            {
                compressedOffset = _compressedOffsets[_sortedDates[index]];
            }
            else
            {
                index = ~index;
                if (index == 0) return startY;
                if (index >= _sortedDates.Count) return startY + (_totalCompressedDuration * scale);

                DateTime d1 = _sortedDates[index - 1];
                DateTime d2 = _sortedDates[index];

                double offset1 = _compressedOffsets[d1];
                double offset2 = _compressedOffsets[d2];

                double ratioInInterval = (d.Ticks - d1.Ticks) / (double)(d2.Ticks - d1.Ticks);
                compressedOffset = offset1 + ratioInInterval * (offset2 - offset1);
            }

            return startY + (compressedOffset * scale);
        }
    }
}
