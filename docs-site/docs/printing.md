# Printing diagrams

Print a diagram on one sheet or across many, as in Cameo's **Print / Print Preview** and EA's **Page Setup / Print
Preview**: choose the paper, the scale and what goes in the header and footer, check the pages in the preview, then
save a PDF, a zip of page pictures, or send it to the browser's print dialog.

## Opening the page setup

- **⌘P** (Ctrl+P) with a diagram open
- right-click the diagram's tab ▸ **Export ▸ Print / PDF…**
- the title-bar project menu ▸ **Export ▸ Print / PDF…**
- **⌘K** ▸ *Print / PDF…*

To print several diagrams into one PDF, right-click a package in the model explorer ▸ **Print diagrams…** (or ⌘K ▸
*Print diagrams…*).

## Page setup

| Option | Choices |
| --- | --- |
| **Paper** | A4, A3, A2, A1, A0, Letter, Legal, Tabloid, or a custom width × height in millimetres |
| **Orientation** | portrait or landscape (landscape is the default: most diagrams are wide) |
| **Margins** | top, right, bottom and left, in millimetres |
| **Scaling** | *Fit to one page*; *Fit to pages* (exactly N across × M down); or a fixed *Scale* such as 100 %, where the number of pages follows. 100 % is the diagram's size on screen at 96 dpi |
| **Overlap** | how many millimetres neighbouring tiles repeat of each other, to trim and tape them together; dashed guides show the overlap |
| **Registration marks** | cross-hair marks in the corners of every tile and its row and column, for assembling a wall chart |
| **Header & footer** | project, diagram name, diagram type, package path, version / branch, date, *Page n of N*, a classification marking and a custom text |
| **Content** | the whole diagram, the *current view* (what the canvas shows), or the *selection only*; the diagram frame; the legend; a light theme for print |

The summary under *Scaling* says how the setup comes out, for example *2 × 1 = 2 pages at 101 %*. A fixed scale that
would need more than 400 pages for one diagram is refused: pick a smaller scale or fit it to fewer pages.

The **header** shows the project and package path on the left, the classification marking in the middle and the diagram
name and type on the right; the **footer** shows the date and version on the left, the classification and custom text
in the middle and the page number on the right. The classification marking starts as the one of the model's report
templates (*Generate document* ▸ *Classification marking*), when there is one.

Prints are **light** by default, even when the canvas is dark (Settings ▸ Appearance ▸ *always-light canvas* changes
only the screen); untick *Light theme for print* to print the canvas's colours.

## Preview

The preview shows the diagram with the page boundaries drawn over it and numbered in print order (across, then down),
and a thumbnail of every page below it, header and footer bands included. It follows each change at once.

Your last page setup is remembered for you on every device. Tick **Save this page setup with the diagram** to store it in
the diagram itself (it is part of the model, so collaborators printing that diagram start from it too).

## Output

| Button | What you get |
| --- | --- |
| **Save PDF** | a multi-page PDF. Diagrams stay **vector**: every page draws the diagram's SVG, so text is real, selectable and searchable text and lines stay sharp at any zoom |
| **PNG pages (zip)** | one PNG per page at 150 dpi (lower for very large sheets, so an A0 page stays within 7,000 pixels) |
| **Print…** | the browser's print dialog with a print-optimised page: `@page` sets the paper size, one sheet per tile. Choose the same paper in the print dialog and *no* margins or scaling there (the page draws its own margins) |

PDFs and zips are written on the server to a temporary file, like [generated documents](document-generation.md), and
stream to your browser, so an A0 diagram with hundreds of shapes does not have to fit in memory or pass through the page.

**Print diagrams…** lists the package's diagrams (at any depth) with a tick box each; click a name to preview it. The
PDF starts with a **contents page** (each diagram with its type, package, number of pages and the page it starts on,
linked) and then prints every diagram with the same page setup. It runs as a background job with progress and **Cancel**;
a long batch keeps running if you close the dialog, and the status bar offers the download when it is ready.

## Desktop app

The desktop app saves the PDF with the system's save dialog. Its web view has no print dialog, so **Print…** saves the
PDF and opens it in your PDF viewer, where you print it.

## Package permissions

Prints show exactly what you may see: shapes of elements in packages hidden from you print as the same nameless
placeholders as on the canvas, and no hidden name, text or id is ever written into the PDF, the pictures or the print
page ([package permissions](permissions.md)).
