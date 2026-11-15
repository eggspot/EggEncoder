namespace EggEncoder.Codecs.H264;

internal sealed class DecodedPictureBuffer
{
    private readonly List<H264Picture> _pictures = new();
    private readonly List<H264Picture> _released = new();
    private int _maxLongTermFrameIndex = -1;
    private int _previousPocMsb;
    private int _previousPocLsb;
    private int _previousFrameNumOffset;
    private int _previousFrameNum;
    private int _previousReferenceFrameNum;
    private bool _previousHadMemoryManagement5;
    private int _nextPictureId;

    public IReadOnlyList<H264Picture> Pictures => _pictures;

    public int NewPictureId() => ++_nextPictureId;

    public List<H264Picture> TakeReleased()
    {
        var released = _released.ToList();
        _released.Clear();
        return released;
    }

    public void ComputePicOrderCount(H264Picture picture, SliceHeader header, SequenceParameterSet sps)
    {
        var maxFrameNum = 1 << sps.Log2MaxFrameNum;
        if (header.IsIdr)
        {
            _previousPocMsb = 0;
            _previousPocLsb = 0;
            _previousFrameNumOffset = 0;
        }
        else if (_previousHadMemoryManagement5)
        {
            _previousPocMsb = 0;
            _previousPocLsb = 0;
            _previousFrameNumOffset = 0;
        }

        if (!header.IsIdr && !sps.GapsInFrameNumAllowed && header.FrameNum != _previousReferenceFrameNum && header.FrameNum != (_previousReferenceFrameNum + 1) % maxFrameNum)
        {
            throw new InvalidDataException("H.264: frame_num jumps, so pictures are missing");
        }

        if (!header.IsIdr && sps.GapsInFrameNumAllowed && header.FrameNum != _previousReferenceFrameNum && header.FrameNum != (_previousReferenceFrameNum + 1) % maxFrameNum)
        {
            throw new NotSupportedException("H.264: gaps in frame_num are not supported");
        }

        var frameNumOffset = header.IsIdr ? 0 : _previousFrameNum > header.FrameNum ? _previousFrameNumOffset + maxFrameNum : _previousFrameNumOffset;
        int top;
        int bottom;
        switch (sps.PicOrderCountType)
        {
            case 0:
                var maxLsb = 1 << sps.Log2MaxPicOrderCountLsb;
                var lsb = header.PicOrderCountLsb;
                var msb = lsb < _previousPocLsb && _previousPocLsb - lsb >= maxLsb / 2 ? _previousPocMsb + maxLsb
                    : lsb > _previousPocLsb && lsb - _previousPocLsb > maxLsb / 2 ? _previousPocMsb - maxLsb
                    : _previousPocMsb;
                top = msb + lsb;
                bottom = top + header.DeltaPicOrderCountBottom;
                if (header.NalReferenceIdc != 0)
                {
                    _previousPocMsb = msb;
                    _previousPocLsb = lsb;
                }

                break;
            case 1:
                var cycle = sps.OffsetForReferenceFrame;
                var absoluteFrameNum = cycle.Length != 0 ? frameNumOffset + header.FrameNum : 0;
                if (header.NalReferenceIdc == 0 && absoluteFrameNum > 0)
                {
                    absoluteFrameNum--;
                }

                var expected = 0;
                if (absoluteFrameNum > 0)
                {
                    var cycleCount = (absoluteFrameNum - 1) / cycle.Length;
                    var inCycle = (absoluteFrameNum - 1) % cycle.Length;
                    expected = cycleCount * cycle.Sum();
                    for (var i = 0; i <= inCycle; i++)
                    {
                        expected += cycle[i];
                    }
                }

                if (header.NalReferenceIdc == 0)
                {
                    expected += sps.OffsetForNonReferencePicture;
                }

                top = expected + header.DeltaPicOrderCount0;
                bottom = top + sps.OffsetForTopToBottomField + header.DeltaPicOrderCount1;
                break;
            default:
                var temp = header.IsIdr ? 0 : header.NalReferenceIdc == 0 ? (2 * (frameNumOffset + header.FrameNum)) - 1 : 2 * (frameNumOffset + header.FrameNum);
                top = temp;
                bottom = temp;
                break;
        }

        picture.PicOrderCount = Math.Min(top, bottom);
        picture.TopPicOrderCount = top;
        picture.BottomPicOrderCount = bottom;
        _previousFrameNumOffset = frameNumOffset;
        _previousFrameNum = header.FrameNum;
        if (header.NalReferenceIdc != 0)
        {
            _previousReferenceFrameNum = header.FrameNum;
        }

        _previousHadMemoryManagement5 = false;
    }

    public H264Picture[] ReferenceListP(H264Picture current, SliceHeader header, SequenceParameterSet sps)
    {
        var maxFrameNum = 1 << sps.Log2MaxFrameNum;
        foreach (var picture in _pictures)
        {
            if (picture.IsReference && !picture.IsLongTerm)
            {
                picture.FrameNumWrap = picture.FrameNum > header.FrameNum ? picture.FrameNum - maxFrameNum : picture.FrameNum;
            }
        }

        var initial = _pictures.Where(p => p.IsReference && !p.IsLongTerm).OrderByDescending(p => p.FrameNumWrap)
            .Concat(_pictures.Where(p => p.IsReference && p.IsLongTerm).OrderBy(p => p.LongTermFrameIndex))
            .ToList();
        return Modify(initial, header.ModificationsL0, header.ReferenceCountL0, header.FrameNum, maxFrameNum);
    }

    public H264Picture[] ReferenceListB(H264Picture current, SliceHeader header, SequenceParameterSet sps, int list)
    {
        var maxFrameNum = 1 << sps.Log2MaxFrameNum;
        var shortTerm = _pictures.Where(p => p.IsReference && !p.IsLongTerm).ToList();
        var before = shortTerm.Where(p => p.PicOrderCount < current.PicOrderCount).OrderByDescending(p => p.PicOrderCount);
        var after = shortTerm.Where(p => p.PicOrderCount > current.PicOrderCount).OrderBy(p => p.PicOrderCount);
        var longTerm = _pictures.Where(p => p.IsReference && p.IsLongTerm).OrderBy(p => p.LongTermFrameIndex);
        var initial = (list == 0 ? before.Concat(after) : after.Concat(before)).Concat(longTerm).ToList();
        if (list == 1 && initial.Count > 1)
        {
            var listZero = shortTerm.Where(p => p.PicOrderCount < current.PicOrderCount).OrderByDescending(p => p.PicOrderCount)
                .Concat(shortTerm.Where(p => p.PicOrderCount > current.PicOrderCount).OrderBy(p => p.PicOrderCount))
                .Concat(longTerm).ToList();
            if (listZero.SequenceEqual(initial))
            {
                (initial[0], initial[1]) = (initial[1], initial[0]);
            }
        }

        foreach (var picture in shortTerm)
        {
            picture.FrameNumWrap = picture.FrameNum > header.FrameNum ? picture.FrameNum - maxFrameNum : picture.FrameNum;
        }

        return Modify(initial, list == 0 ? header.ModificationsL0 : header.ModificationsL1, list == 0 ? header.ReferenceCountL0 : header.ReferenceCountL1, header.FrameNum, maxFrameNum);
    }

    public void Mark(H264Picture current, SliceHeader header, SequenceParameterSet sps)
    {
        if (!current.IsReference)
        {
            return;
        }

        var maxFrameNum = 1 << sps.Log2MaxFrameNum;
        if (header.IsIdr)
        {
            foreach (var picture in _pictures)
            {
                picture.IsReference = false;
            }

            if (header.LongTermReference)
            {
                current.IsLongTerm = true;
                current.LongTermFrameIndex = 0;
                _maxLongTermFrameIndex = 0;
            }
            else
            {
                _maxLongTermFrameIndex = -1;
            }

            return;
        }

        if (header.MemoryManagement is { } operations)
        {
            foreach (var operation in operations)
            {
                ApplyMemoryManagement(current, header, operation, maxFrameNum);
            }
        }
        else
        {
            SlidingWindow(sps);
        }

        if (!current.IsLongTerm)
        {
            var references = _pictures.Count(p => p.IsReference);
            if (references >= Math.Max(sps.MaxNumberOfReferenceFrames, 1))
            {
                throw new InvalidDataException("H.264: more reference frames than the sequence allows");
            }
        }
    }

    public IEnumerable<H264Picture> Store(H264Picture current, SliceHeader header, SequenceParameterSet sps)
    {
        var output = new List<H264Picture>();
        if (header.IsIdr || current.HasMemoryManagement5)
        {
            foreach (var picture in _pictures.Where(p => p.NeedsOutput).OrderBy(p => p.PicOrderCount))
            {
                if (!header.NoOutputOfPriorPictures)
                {
                    output.Add(picture);
                }

                picture.NeedsOutput = false;
            }

            RemoveUnused(output);
        }

        RemoveUnused(output);
        var capacity = sps.DecodedPictureBufferSize;
        while (_pictures.Count >= capacity)
        {
            if (!Bump(output))
            {
                throw new InvalidDataException("H.264: the decoded picture buffer is full of reference pictures");
            }
        }

        current.NeedsOutput = true;
        _pictures.Add(current);
        var reorder = sps.OutputReorderLimit;
        if (reorder is { } limit)
        {
            while (_pictures.Count(p => p.NeedsOutput) > limit && Bump(output))
            {
            }
        }

        return output;
    }

    public IEnumerable<H264Picture> Flush()
    {
        var output = new List<H264Picture>();
        while (Bump(output))
        {
        }

        RemoveUnused(output);
        return output;
    }

    public void Clear()
    {
        _released.AddRange(_pictures);
        _pictures.Clear();
    }

    private bool Bump(List<H264Picture> output)
    {
        H264Picture? next = null;
        foreach (var picture in _pictures)
        {
            if (picture.NeedsOutput && (next is null || picture.PicOrderCount < next.PicOrderCount))
            {
                next = picture;
            }
        }

        if (next is null)
        {
            return false;
        }

        next.NeedsOutput = false;
        output.Add(next);
        RemoveUnused(output);
        return true;
    }

    private void RemoveUnused(List<H264Picture> output)
    {
        foreach (var picture in _pictures.Where(p => !p.IsReference && !p.NeedsOutput).ToList())
        {
            _pictures.Remove(picture);
            _released.Add(picture);
        }
    }

    private void SlidingWindow(SequenceParameterSet sps)
    {
        var shortTerm = _pictures.Where(p => p.IsReference && !p.IsLongTerm).ToList();
        var longTermCount = _pictures.Count(p => p.IsReference && p.IsLongTerm);
        if (shortTerm.Count + longTermCount >= Math.Max(sps.MaxNumberOfReferenceFrames, 1) && shortTerm.Count > 0)
        {
            shortTerm.MinBy(p => p.FrameNumWrap)!.IsReference = false;
        }
    }

    private void ApplyMemoryManagement(H264Picture current, SliceHeader header, MemoryManagementOperation operation, int maxFrameNum)
    {
        foreach (var picture in _pictures.Where(p => p.IsReference && !p.IsLongTerm))
        {
            picture.FrameNumWrap = picture.FrameNum > header.FrameNum ? picture.FrameNum - maxFrameNum : picture.FrameNum;
        }

        switch (operation.Operation)
        {
            case 1:
                var picNum = header.FrameNum - (operation.DifferenceOfPicNumsMinus1 + 1);
                Unmark(_pictures.FirstOrDefault(p => p.IsReference && !p.IsLongTerm && p.FrameNumWrap == picNum));
                break;
            case 2:
                Unmark(_pictures.FirstOrDefault(p => p.IsReference && p.IsLongTerm && p.LongTermFrameIndex == operation.LongTermPicNum));
                break;
            case 3:
                var target = header.FrameNum - (operation.DifferenceOfPicNumsMinus1 + 1);
                var picture = _pictures.FirstOrDefault(p => p.IsReference && !p.IsLongTerm && p.FrameNumWrap == target);
                Unmark(_pictures.FirstOrDefault(p => p.IsReference && p.IsLongTerm && p.LongTermFrameIndex == operation.LongTermFrameIndex));
                if (picture is not null)
                {
                    picture.IsLongTerm = true;
                    picture.LongTermFrameIndex = operation.LongTermFrameIndex;
                }

                break;
            case 4:
                _maxLongTermFrameIndex = operation.MaxLongTermFrameIndexPlus1 - 1;
                foreach (var longTerm in _pictures.Where(p => p.IsReference && p.IsLongTerm && p.LongTermFrameIndex > _maxLongTermFrameIndex).ToList())
                {
                    Unmark(longTerm);
                }

                break;
            case 5:
                foreach (var reference in _pictures)
                {
                    Unmark(reference);
                }

                _maxLongTermFrameIndex = -1;
                current.HasMemoryManagement5 = true;
                _previousHadMemoryManagement5 = true;
                current.FrameNum = 0;
                _previousFrameNum = 0;
                _previousReferenceFrameNum = 0;
                current.PicOrderCount -= Math.Min(current.TopPicOrderCount, current.BottomPicOrderCount);
                current.TopPicOrderCount = current.PicOrderCount;
                break;
            case 6:
                Unmark(_pictures.FirstOrDefault(p => p.IsReference && p.IsLongTerm && p.LongTermFrameIndex == operation.LongTermFrameIndex));
                current.IsLongTerm = true;
                current.LongTermFrameIndex = operation.LongTermFrameIndex;
                break;
        }
    }

    private static void Unmark(H264Picture? picture)
    {
        if (picture is null)
        {
            return;
        }

        picture.IsReference = false;
        picture.IsLongTerm = false;
        picture.LongTermFrameIndex = -1;
    }

    private static H264Picture[] Modify(List<H264Picture> initial, ReferenceListModification[] modifications, int count, int currentFrameNum, int maxFrameNum)
    {
        var list = new List<H264Picture?>(initial);
        while (list.Count < count)
        {
            list.Add(null);
        }

        if (list.Count > count)
        {
            list.RemoveRange(count, list.Count - count);
        }

        var predicted = currentFrameNum;
        var index = 0;
        foreach (var modification in modifications)
        {
            if (index >= count)
            {
                throw new InvalidDataException("H.264: more reference list modifications than list entries");
            }

            H264Picture? target;
            if (modification.Operation is 0 or 1)
            {
                var difference = modification.Value + 1;
                var noWrap = modification.Operation == 0 ? predicted - difference : predicted + difference;
                if (noWrap < 0)
                {
                    noWrap += maxFrameNum;
                }
                else if (noWrap >= maxFrameNum)
                {
                    noWrap -= maxFrameNum;
                }

                predicted = noWrap;
                var picNum = noWrap > currentFrameNum ? noWrap - maxFrameNum : noWrap;
                target = initial.FirstOrDefault(p => !p.IsLongTerm && p.FrameNumWrap == picNum)
                    ?? throw new InvalidDataException("H.264: a reference list modification names a missing picture");
            }
            else
            {
                target = initial.FirstOrDefault(p => p.IsLongTerm && p.LongTermFrameIndex == modification.Value)
                    ?? throw new InvalidDataException("H.264: a reference list modification names a missing long-term picture");
            }

            list.Insert(index, target);
            index++;
            for (var i = index; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], target))
                {
                    list.RemoveAt(i);
                    break;
                }
            }

            if (list.Count > count)
            {
                list.RemoveRange(count, list.Count - count);
            }
        }

        return list.Select(p => p!).ToArray();
    }
}
