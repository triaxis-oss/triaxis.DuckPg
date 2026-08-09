CREATE TABLE [dbo].[orders] (
    [order_id] INT            NOT NULL,
    [amount]   DECIMAL (9, 2) NOT NULL,
    [note]     NVARCHAR (50)  NULL,
    CONSTRAINT [PK_orders] PRIMARY KEY CLUSTERED ([order_id] ASC)
);
GO

CREATE TABLE [dbo].[lines] (
    [line_id]  INT NOT NULL IDENTITY (1, 1),
    [order_id] INT NOT NULL,
    [qty]      INT NOT NULL,
    CONSTRAINT [PK_lines] PRIMARY KEY CLUSTERED ([line_id] ASC),
    CONSTRAINT [FK_lines_orders] FOREIGN KEY ([order_id]) REFERENCES [dbo].[orders] ([order_id]) ON DELETE CASCADE
);
GO

-- A single RETURN over the parameters: what a macro can be.
CREATE FUNCTION [dbo].[Doubled] (@value INT)
RETURNS INT
AS
BEGIN
    RETURN @value * 2
END
GO

-- String concatenation, a NULL guard and a cast: the operators whose meaning differs.
CREATE FUNCTION [dbo].[NoteOf] (@note NVARCHAR (50), @order_id INT)
RETURNS NVARCHAR (100)
AS
BEGIN
    RETURN ISNULL(@note, N'none') + N'#' + CAST(@order_id AS NVARCHAR (10))
END
GO

-- One function calling another. Named to sort *before* the one it calls, because the model lists
-- its elements alphabetically -- so the order they can be created in is not the order they arrive.
CREATE FUNCTION [dbo].[Amplified] (@value INT)
RETURNS INT
AS
BEGIN
    RETURN [dbo].[Doubled](@value) * 2
END
GO

-- Declared narrower than the arithmetic that fills it: SQL Server answers a smallint where DuckDB
-- would answer an INTEGER, and an application reading the scalar as a short throws on that.
CREATE FUNCTION [dbo].[Small] (@value INT)
RETURNS SMALLINT
AS
BEGIN
    RETURN @value * 2
END
GO

-- No parameters at all.
CREATE FUNCTION [dbo].[Answer] ()
RETURNS INT
AS
BEGIN
    RETURN 42
END
GO

-- A branch and a variable: a procedure, not an expression, and so not publishable.
CREATE FUNCTION [dbo].[Bucketed] (@value INT)
RETURNS INT
AS
BEGIN
    DECLARE @out INT = 0;
    IF @value > 10 SET @out = 1;
    RETURN @out
END
GO

-- A view calling one of them, to prove the macros are there before the views are built.
CREATE VIEW [dbo].[doubled_orders]
AS
SELECT [order_id], [dbo].[Doubled]([order_id]) AS [doubled] FROM [dbo].[orders];
GO

-- The other two delete actions, so the numbers DacFx encodes them as are read off DacFx's own
-- output rather than off the test helper, which could spell them wrong in exactly the same way.
CREATE TABLE [dbo].[notes] (
    [note_id]  INT NOT NULL,
    [order_id] INT NULL,
    CONSTRAINT [PK_notes] PRIMARY KEY CLUSTERED ([note_id] ASC),
    CONSTRAINT [FK_notes_orders] FOREIGN KEY ([order_id]) REFERENCES [dbo].[orders] ([order_id]) ON DELETE SET NULL
);
GO

CREATE TABLE [dbo].[tags] (
    [tag_id]   INT NOT NULL,
    [order_id] INT NULL CONSTRAINT [DF_tags_order_id] DEFAULT ((0)),
    CONSTRAINT [PK_tags] PRIMARY KEY CLUSTERED ([tag_id] ASC),
    CONSTRAINT [FK_tags_orders] FOREIGN KEY ([order_id]) REFERENCES [dbo].[orders] ([order_id]) ON DELETE SET DEFAULT
);
GO

-- Uniqueness past the key, in both the forms DacFx writes it: a UNIQUE constraint names its table
-- through a relationship, a unique index carries the table in its own name. The plain index is here
-- so that "not unique" is read off an absent property rather than off a false one.
CREATE TABLE [dbo].[codes] (
    [code_id] INT           NOT NULL,
    [label]   NVARCHAR (20) NOT NULL,
    [region]  NVARCHAR (10) NOT NULL,
    [slot]    INT           NULL,
    CONSTRAINT [PK_codes] PRIMARY KEY CLUSTERED ([code_id] ASC),
    CONSTRAINT [UQ_codes_label] UNIQUE ([label] ASC),
    CONSTRAINT [UQ_codes_region_slot] UNIQUE ([region] ASC, [slot] ASC)
);
GO

CREATE UNIQUE INDEX [IX_codes_slot] ON [dbo].[codes] ([slot] ASC);
GO

CREATE INDEX [IX_codes_region] ON [dbo].[codes] ([region] ASC);
GO
