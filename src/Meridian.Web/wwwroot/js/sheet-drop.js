// Spreadsheet files (.xlsx / .csv / .tsv) dropped on an element marked data-sheet-drop="<input id>" are handed to that
// hidden <InputFile>, which opens the spreadsheet import dialog (Workbench.Spreadsheet.cs). Other drops are left alone.
(() => {
    const pattern = /\.(xlsx|xlsm|csv|tsv)$/i;
    const zoneOf = e => e.target instanceof Element ? e.target.closest('[data-sheet-drop]') : null;
    const hasFiles = e => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');

    document.addEventListener('dragover', e => {
        const zone = zoneOf(e);
        if (!zone || !hasFiles(e)) return;
        e.preventDefault();
        e.stopPropagation();
        e.dataTransfer.dropEffect = 'copy';
        zone.classList.add('sheet-drop-over');
    }, true);

    document.addEventListener('dragleave', e => {
        const zone = zoneOf(e);
        if (zone && !zone.contains(e.relatedTarget)) zone.classList.remove('sheet-drop-over');
    }, true);

    document.addEventListener('drop', e => {
        const zone = zoneOf(e);
        if (!zone || !hasFiles(e)) return;
        zone.classList.remove('sheet-drop-over');
        const file = Array.from(e.dataTransfer.files || []).find(f => pattern.test(f.name));
        if (!file) return;
        e.preventDefault();
        e.stopPropagation();
        const input = document.getElementById(zone.getAttribute('data-sheet-drop'));
        if (!input) return;
        const dt = new DataTransfer();
        dt.items.add(file);
        input.files = dt.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }, true);
})();
