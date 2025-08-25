Planned JS helpers (to be implemented in a proper JS file and included in index.html):

window.continueDb = {
  // Open db and ensure object stores exist
  open: async function() {},
  // Export all content + save
  exportAllToDownload: async function() {},
  // Import from <input type=file>
  importFromFileInput: async function() {}
};

Expected stores per continue-project.md §5.4:
- games(id)
- achievements(id) with index by_gameId
- cards(id)
- levels(index)
- save(singleton)
