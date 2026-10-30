Option Strict On
Option Explicit On

''' <summary>A book in the demo library.</summary>
Public Class Book
    Implements IEquatable(Of Book)

    Public Sub New(bookId As Integer, bookTitle As String, bookAuthor As String, bookYear As Integer)
        Id = bookId
        Title = bookTitle
        Author = bookAuthor
        Year = bookYear
    End Sub

    Public ReadOnly Property Id As Integer
    Public ReadOnly Property Title As String
    Public ReadOnly Property Author As String
    Public ReadOnly Property Year As Integer

    Public Overloads Function Equals(other As Book) As Boolean Implements IEquatable(Of Book).Equals
        If other Is Nothing Then Return False
        Return Id = other.Id
    End Function

    Public Overrides Function Equals(obj As Object) As Boolean
        Return Equals(TryCast(obj, Book))
    End Function

    Public Overrides Function GetHashCode() As Integer
        Return Id.GetHashCode()
    End Function

    Public Overrides Function ToString() As String
        Return $"{Title} ({Year}) by {Author}"
    End Function

End Class
