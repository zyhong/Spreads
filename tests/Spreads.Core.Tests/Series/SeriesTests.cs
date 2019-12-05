// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using NUnit.Framework;
using ObjectLayoutInspector;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Spreads.Core.Tests.Series
{
    [Category("CI")]
    [TestFixture]
    public unsafe class SeriesTests
    {
        [Test, Explicit("output")]
        public void SeriesObjectSize()
        {
            TypeLayout.PrintLayout<Series<DateTime, double>>();
        }

        [Test]
        [SuppressMessage("ReSharper", "PossibleInvalidOperationException")]
        public void NewSeries()
        {
            var s = new Series<int, int>(Array.Empty<int>(), Array.Empty<int>());
            Assert.AreEqual(s.Mutability, Mutability.ReadOnly);
            Assert.AreEqual(s.KeySorting, KeySorting.Strong);
            Assert.AreEqual(0, s.RowCount.Value);
            s.Dispose();
            Assert.IsTrue(s.IsDisposed);

            var aps = new AppendSeries<int, int>();
            Assert.AreEqual(aps.Mutability, Mutability.AppendOnly);
            Assert.AreEqual(aps.KeySorting, KeySorting.Strong);
            Assert.AreEqual(0, aps.RowCount.Value);
            aps.Dispose();
            Assert.IsTrue(aps.IsDisposed);

            var mus = new MutableSeries<int, int>();
            Assert.AreEqual(mus.Mutability, Mutability.Mutable);
            Assert.AreEqual(mus.KeySorting, KeySorting.Strong);
            Assert.AreEqual(0, mus.RowCount.Value);
            mus.MarkAppendOnly();
            mus.MarkAppendOnly(); // ignored, does not throw
            Assert.AreEqual(Mutability.AppendOnly, mus.Mutability);
            mus.MarkReadOnly();
            mus.MarkReadOnly(); // ignored, does not throw
            Assert.AreEqual(Mutability.ReadOnly, mus.Mutability);
            Assert.Throws<InvalidOperationException>(() => { mus.MarkAppendOnly(); });
            mus.Dispose();
            Assert.IsTrue(mus.IsDisposed);
        }
    }
}