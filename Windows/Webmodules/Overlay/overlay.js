// Overlay WebModule - Display a single card centered and filling the screen
(function() {
  const api = window.webapi;
  
  // Initialize on load - no longer auto-loads a card
  document.addEventListener('DOMContentLoaded', () => {
    console.log('Overlay ready - waiting for menu hover events');
    
    // Add click handler to close menus when clicking anywhere on the overlay
    document.addEventListener('click', async (e) => {
      try {
        if (!api?.ui?.closeMenus) {
          throw new Error('webapi helper not loaded');
        }

        const data = await api.ui.closeMenus();

        if (data?.success !== false) {
          console.log('Menu close requested');
        }
      } catch (error) {
        console.error('Failed to request menu close:', error);
      }
    });
    
    // Add double-click handler to toggle fullscreen
    document.addEventListener('dblclick', async (e) => {
      try {
        if (!api?.ui?.toggleFullscreen) {
          throw new Error('webapi helper not loaded');
        }

        const data = await api.ui.toggleFullscreen();

        if (data?.success !== false) {
          console.log('Fullscreen toggle requested');
        }
      } catch (error) {
        console.error('Failed to request fullscreen toggle:', error);
      }
    });
  });
  
  /**
   * Load game save to get owned cores
   */
  async function loadGameSave() {
    if (typeof window.loadGameSave === 'function') {
      return await window.loadGameSave();
    }
    // Fallback if shared module not loaded
    const save = localStorage.getItem('brokenNesGameSave');
    if (save) {
      return JSON.parse(save);
    }
    return {
      ownedCpuIds: ['FMC'],
      ownedPpuIds: ['FMC'],
      ownedApuIds: ['FMC'],
      ownedShaderIds: ['PX']
    };
  }
  
  /**
   * Load and display a single card
   * Displays the first owned CPU core from the game save
   */
  async function loadAndDisplayCard() {
    try {
      // Load game save to get owned cores
      const save = await loadGameSave();
      console.log('Game save loaded:', save);
      
      // Try to display first owned CPU core
      if (save.ownedCpuIds && save.ownedCpuIds.length > 0) {
        const coreId = save.ownedCpuIds[0];
        console.log(`Attempting to display CPU core: ${coreId}`);
        await displayCard('cpu', coreId);
        return;
      }
      
      // Fallback to PPU if no CPU cores
      if (save.ownedPpuIds && save.ownedPpuIds.length > 0) {
        const coreId = save.ownedPpuIds[0];
        console.log(`Attempting to display PPU core: ${coreId}`);
        await displayCard('ppu', coreId);
        return;
      }
      
      // Fallback to APU if no PPU cores
      if (save.ownedApuIds && save.ownedApuIds.length > 0) {
        const coreId = save.ownedApuIds[0];
        console.log(`Attempting to display APU core: ${coreId}`);
        await displayCard('apu', coreId);
        return;
      }
      
      // Fallback to shader if no other cores
      if (save.ownedShaderIds && save.ownedShaderIds.length > 0) {
        const coreId = save.ownedShaderIds[0];
        console.log(`Attempting to display Shader: ${coreId}`);
        await displayCard('shader', coreId);
        return;
      }
      
      console.error('No owned cores available to display');
    } catch (error) {
      console.error('Error loading card:', error);
    }
  }
  
  /**
   * Display a card by fetching its SVG from the API
   * @param {string} domain - Core domain (cpu, ppu, apu, shader, etc.)
   * @param {string} id - Core ID
   */
  async function displayCard(domain, id) {
    try {
      // Fetch card SVG from API using correct endpoint format
      if (!api?.card?.getUrl || !api?.card?.getSvg) {
        throw new Error('webapi helper not loaded');
      }

      const url = api.card.getUrl(domain, id);
      console.log(`Fetching card from: ${url}`);

      const result = await api.card.getSvg(domain, id);

      if (!result?.success) {
        throw new Error(result?.error || 'Failed to fetch card');
      }

      const svgContent = result.text;
      
      // Display the card in the container
      const container = document.getElementById('card-container');
      if (container) {
        container.innerHTML = svgContent;
        console.log(`Successfully displayed card: ${domain}/${id}`);
      }
    } catch (error) {
      console.error('Error displaying card:', error);
      // Show error message in the container
      const container = document.getElementById('card-container');
      if (container) {
        container.innerHTML = `<div style="color: white; font-family: 'Press Start 2P', monospace; font-size: 12px; text-align: center;">Error loading card: ${error.message}</div>`;
      }
    }
  }
  
  /**
   * Clear the card display
   */
  function clearCard() {
    const container = document.getElementById('card-container');
    if (container) {
      container.innerHTML = '';
      console.log('Card display cleared');
    }
  }
  
  // Expose functions globally so they can be called from C# via ExecuteScriptAsync
  window.displayCard = displayCard;
  window.clearCard = clearCard;
})();
