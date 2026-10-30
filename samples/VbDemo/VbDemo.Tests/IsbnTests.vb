Option Strict On
Option Explicit On

Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports VbDemo.Core

<TestClass>
Public Class IsbnTests

    <TestMethod>
    Public Sub Normalize_RemovesSeparators()
        Assert.AreEqual("0306406152", Isbn.Normalize("0-306-40615-2"))
    End Sub

    <TestMethod>
    Public Sub IsValid_AcceptsAKnownChecksum()
        Assert.IsTrue(Isbn.IsValid("0-306-40615-2"))
    End Sub

    <TestMethod>
    Public Sub IsValid_RejectsAChangedDigit()
        Assert.IsFalse(Isbn.IsValid("0-306-40615-3"))
    End Sub

    <TestMethod>
    Public Sub Checksum_RejectsTheWrongLength()
        Assert.AreEqual(-1, Isbn.Checksum("123"))
        Assert.AreEqual(-1, Isbn.Checksum(Nothing))
    End Sub

End Class
