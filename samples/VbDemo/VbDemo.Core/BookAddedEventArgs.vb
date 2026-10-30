Option Strict On
Option Explicit On

''' <summary>The payload of <see cref="ILibraryService.BookAdded"/>.</summary>
Public Class BookAddedEventArgs
    Inherits EventArgs

    Public Sub New(addedBook As Book)
        Book = addedBook
    End Sub

    Public ReadOnly Property Book As Book

End Class
