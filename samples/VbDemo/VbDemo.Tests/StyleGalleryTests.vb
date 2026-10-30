Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports VbDemo.Core

''' <summary>
''' Pins the behaviour of every member of <see cref="StyleGallery"/>. The style demo rewrites that
''' file; these tests are what proves the rewrite kept the meaning.
''' </summary>
<TestClass>
Public Class StyleGalleryTests

    <TestMethod>
    Public Sub ShortNames_TrimsEveryTitle()
        Dim result = StyleGallery.ShortNames(New List(Of String) From {"  a  ", "b"})

        CollectionAssert.AreEqual(New List(Of String) From {"a", "b"}, result)
    End Sub

    <TestMethod>
    Public Sub SharedAccess_ReadsTheSharedField()
        Assert.AreEqual(7, StyleGallery.SharedAccess())
    End Sub

    <TestMethod>
    Public Sub DaysInYear_UnderstandsLeapYears()
        Assert.AreEqual(366, StyleGallery.DaysInYear(2024))
        Assert.AreEqual(365, StyleGallery.DaysInYear(2023))
    End Sub

    <TestMethod>
    Public Sub IsMissing_RecognisesNothing()
        ' Only the Nothing case is pinned: with an instance of a type that defines no "=", the
        ' legacy late-bound comparison throws at run time. That is the bug IDE0041 rewrites.
        Assert.IsTrue(StyleGallery.IsMissing(Nothing))
    End Sub

    <TestMethod>
    Public Sub OrElseDefault_ReplacesNothing()
        Dim book = StyleGallery.OrElseDefault(Nothing)

        Assert.AreEqual("Untitled", book.Title)
    End Sub

    <TestMethod>
    Public Sub TitleOrNothing_PropagatesNothing()
        Assert.IsNull(StyleGallery.TitleOrNothing(Nothing))
        Assert.AreEqual("Clean Code", StyleGallery.TitleOrNothing(New Book(1, "Clean Code", "Martin", 2008)))
    End Sub

    <TestMethod>
    Public Sub OrZero_ReplacesNothing()
        Assert.AreEqual(0, StyleGallery.OrZero(Nothing).Value)
        Assert.AreEqual(5, StyleGallery.OrZero(5).Value)
    End Sub

    <TestMethod>
    Public Sub Parenthesized_ReturnsTheTitle()
        Assert.AreEqual("Clean Code", StyleGallery.Parenthesized(New Book(1, "Clean Code", "Martin", 2008)))
    End Sub

    <TestMethod>
    Public Sub CountUp_Counts()
        Assert.AreEqual(4, StyleGallery.CountUp(4))
    End Sub

    <TestMethod>
    Public Sub IsAdult_UsesTheAge()
        Assert.IsTrue(StyleGallery.IsAdult(18))
        Assert.IsFalse(StyleGallery.IsAdult(17))
    End Sub

    <TestMethod>
    Public Sub IsPositive_ReportsTheSign()
        Assert.IsTrue(StyleGallery.IsPositive(1))
        Assert.IsFalse(StyleGallery.IsPositive(0))
    End Sub

    <TestMethod>
    Public Sub IsDifferent_ComparesReferences()
        Dim book As New Book(1, "Clean Code", "Martin", 2008)

        Assert.IsFalse(StyleGallery.IsDifferent(book, book))
        Assert.IsTrue(StyleGallery.IsDifferent(book, Nothing))
    End Sub

    <TestMethod>
    Public Sub Add_Adds()
        Assert.AreEqual(5, StyleGallery.Add(2, 3))
    End Sub

    <TestMethod>
    Public Sub Identity_ReturnsTheValue()
        Assert.AreEqual(42, StyleGallery.Identity(42))
    End Sub

    <TestMethod>
    Public Sub Summary_RendersUppercase()
        Assert.AreEqual("HELLO", New Summary("hello").Render())
    End Sub

End Class
